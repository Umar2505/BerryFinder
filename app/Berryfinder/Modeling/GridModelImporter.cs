using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;

// Imports the grid scored by notebooks/predict_region.py (see Core/GridModel.cs) and publishes picking areas from it
public sealed class GridModelImporter(OpenDb openDb, Func<string, Task<byte[]>> readData, PredictionPublisher publisher)
{
    public const string CardPath = "ml/berry_model_card.json";

    public async Task<ModelCard?> TryReadCardAsync()
    {
        try
        {
            return ModelCard.Parse(await readData(CardPath));
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"No grid model card at data/{CardPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> IsImportedAsync(string version)
    {
        await using var db = await openDb();
        return await db.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM model_versions WHERE kind = @kind AND version = @version)
               AND EXISTS (SELECT 1 FROM model_grid_cells WHERE run_version = @version)
            """, new { kind = ModelGate.Kind, version });
    }

    public async Task ImportAsync(JobProgress progress, CancellationToken ct)
    {
        var card = await TryReadCardAsync() ?? throw new InvalidOperationException($"data/{CardPath} is missing or unreadable");
        await progress.ReportAsync(0.02, $"reading {card.GridFile}");

        IReadOnlyList<GridCell> cells;
        await using (var gz = new GZipStream(new MemoryStream(await readData(card.GridFile)), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
        {
            cells = GridCsv.Parse(reader, card.Species.Keys.ToList());
        }

        progress.RecordsIn += cells.Count;
        DateTime started;
        await using (var db = await openDb())
        {
            started = await db.ExecuteScalarAsync<DateTime>("SELECT now()");
        }

        const int batchSize = 2000;
        for (var i = 0; i < cells.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new JsonArray();
            foreach (var c in cells.Skip(i).Take(batchSize))
            {
                var scores = new JsonObject();
                foreach (var (s, v) in c.Scores)
                {
                    scores[s] = new JsonObject { ["idx"] = v.Index, ["p"] = v.Probability, ["sd"] = v.Spread, ["rule"] = v.Rule };
                }

                var habitat = new JsonObject();
                foreach (var (k, v) in c.Habitat)
                {
                    habitat[k] = v;
                }

                rows.Add(new JsonObject { ["x"] = c.X, ["y"] = c.Y, ["ood"] = c.Ood, ["s"] = scores, ["h"] = habitat });
            }

            await using var db = await openDb();
            progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO model_grid_cells (run_version, cell_x, cell_y, step_m, ood, scores, habitat, geom, centroid)
                SELECT @run, (c->>'x')::int, (c->>'y')::int, @step, (c->>'ood')::boolean, c->'s', c->'h',
                    ST_MakeEnvelope((c->>'x')::int - @half, (c->>'y')::int - @half, (c->>'x')::int + @half, (c->>'y')::int + @half, 3067),
                    ST_Transform(ST_SetSRID(ST_MakePoint((c->>'x')::int, (c->>'y')::int), 3067), 4326)
                FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS c
                ON CONFLICT (run_version, cell_x, cell_y) DO UPDATE SET step_m = EXCLUDED.step_m, ood = EXCLUDED.ood, scores = EXCLUDED.scores,
                    habitat = EXCLUDED.habitat, geom = EXCLUDED.geom, centroid = EXCLUDED.centroid, imported_at = now()
                """, new { run = card.Version, step = card.GridStepM, half = card.GridStepM / 2, rows = rows.ToJsonString() }, commandTimeout: 300, cancellationToken: ct));
            await progress.ReportAsync(0.05 + 0.45 * Math.Min(1.0, (i + batchSize) / (double)cells.Count), $"{Math.Min(cells.Count, i + batchSize)}/{cells.Count} grid cells stored");
        }

        var versions = new List<(string Species, long Id, string Status)>();
        await using (var db = await openDb())
        {
            // A re-scored grid replaces the old one: cells this import did not touch are gone from the file
            await db.ExecuteAsync("DELETE FROM model_grid_cells WHERE run_version = @run AND imported_at < @started", new { run = card.Version, started });

            foreach (var (species, m) in card.Species)
            {
                if (!BerryCatalog.TryGet(species, out _))
                {
                    progress.Warnings.Add($"model card lists unknown species '{species}'");
                    continue;
                }

                var (status, notes) = ModelGate.Decide(m, Species.Supported.Contains(species));
                var metrics = JsonSerializer.Serialize(new
                {
                    EligibleForPromotion = status != "rejected",
                    m.RulesAuc, m.RulesBoyce, m.GbifAuc, m.GbifBoyce, m.GbifRecallTop20, m.GbifPresences,
                    Members = card.ValidationAucMembers.Count,
                    ValidationAuc = card.ValidationAucMembers.Count == 0 ? (double?)null : Math.Round(card.ValidationAucMembers.Average(), 3),
                });
                var parameters = JsonSerializer.Serialize(new
                {
                    run_version = card.Version, run = card.Run, trained_on = card.TrainedOn, label_mode = card.LabelMode, grid_file = card.GridFile,
                    grid_step_m = card.GridStepM, model_file = card.ModelFile, model_sha256 = card.ModelSha256,
                    min_index = ModelGate.MinIndex, high_index = ModelGate.HighIndex, regional_share = ModelGate.RegionalShare,
                    regional_high_share = ModelGate.RegionalHighShare, habitat_guard = true,
                });

                // The first import sets the status; a later import of the same version keeps whatever an admin decided since
                var row = await db.QuerySingleAsync<(long Id, string Status)>("""
                    INSERT INTO model_versions (species, layer, kind, version, status, params, metrics, training_rows, notes, created_by)
                    VALUES (@species, 'habitat', @kind, @version, @initial, CAST(@parameters AS jsonb), CAST(@metrics AS jsonb), @rows, @notes, 'system')
                    ON CONFLICT (species, layer, version) DO UPDATE SET params = EXCLUDED.params, metrics = EXCLUDED.metrics, notes = EXCLUDED.notes,
                        training_rows = EXCLUDED.training_rows
                    RETURNING id, status
                    """, new { species, kind = ModelGate.Kind, version = card.Version, initial = status == "production" ? "candidate" : status, parameters, metrics, rows = card.TrainingRows, notes });

                var current = row.Status;
                if (status == "production" && row.Status == "candidate")
                {
                    // A newer grid model replaces an older one the system promoted; anything an admin promoted stays
                    await db.ExecuteAsync("""
                        UPDATE model_versions SET status = 'retired'
                        WHERE species = @species AND layer = 'habitat' AND status = 'production' AND kind = @kind AND promoted_by = 'system' AND id <> @id
                        """, new { species, kind = ModelGate.Kind, id = row.Id });
                    var promoted = await db.ExecuteAsync("""
                        UPDATE model_versions SET status = 'production', promoted_at = now(), promoted_by = 'system'
                        WHERE id = @id AND NOT EXISTS (SELECT 1 FROM model_versions WHERE species = @species AND layer = 'habitat' AND status = 'production')
                        """, new { id = row.Id, species });
                    current = promoted > 0 ? "production" : row.Status;
                }

                await Audit.WriteAsync(db, "system", "model.import", $"model:{row.Id}", new { species, card.Version, status = current, m.GbifAuc });
                versions.Add((species, row.Id, current));
            }
        }

        await publisher.RefreshProductionAsync();
        var publish = versions.Where(v => v.Status is not ("rejected" or "retired")).ToList();
        for (var i = 0; i < publish.Count; i++)
        {
            var (species, id, _) = publish[i];
            var k = i;
            await publisher.PublishAsync(species, id, new JobProgress((f, msg) => progress.ReportAsync(0.5 + 0.5 * (k + f) / publish.Count, $"{species}: {msg}")), ct);
        }

        publisher.NotifyPublished();
        await progress.ReportAsync(1, $"{cells.Count} cells, {versions.Count(v => v.Status == "production")} berries in production");
    }
}
