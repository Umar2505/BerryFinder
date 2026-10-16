using System.Text.Json;
using Dapper;

// Builds the labelled data set from field observations, runs TrainingPipeline, and records every run in
// the model registry, including runs that could not train for lack of data. Promotion is a separate,
// explicit admin action that refuses any model that did not pass validation.
public sealed class TrainingService(OpenDb openDb, Func<string, string, Task> writeArtifact, Func<long, Task> onPromoted)
{
    private sealed record LabelRow(long Id, string UserId, string Method, string Outcome, int? EffortMinutes, int Season, long StandId, string TileId);

    public async Task TrainAllAsync(string? actor, JobProgress progress, CancellationToken ct)
    {
        for (var i = 0; i < Species.Supported.Length; i++)
        {
            var species = Species.Supported[i];
            await progress.ReportAsync((double)i / Species.Supported.Length, $"training {species}");
            await TrainAsync(species, actor, progress, ct);
        }
    }

    public async Task<long> TrainAsync(string species, string? actor, JobProgress progress, CancellationToken ct)
    {
        List<LabelRow> labels;
        Dictionary<long, StandRow> stands;

        await using (var db = await openDb())
        {
            // Accepted, consented, non-duplicate observations inside an ingested stand; one per contributor,
            // stand and season so repeat visits by one person do not count as independent evidence
            labels = (await db.QueryAsync<LabelRow>("""
                SELECT DISTINCT ON (o.user_id, st.stand_id, EXTRACT(YEAR FROM o.observed_on))
                    o.id, o.user_id AS UserId, o.method, o.outcome, o.effort_minutes AS EffortMinutes,
                    EXTRACT(YEAR FROM o.observed_on)::int AS Season, st.stand_id AS StandId, st.tile_id AS TileId
                FROM field_observations o
                JOIN LATERAL (SELECT s.stand_id, s.tile_id FROM forest_stands s WHERE ST_Intersects(s.geom, ST_Transform(o.geom, 3067)) LIMIT 1) st ON true
                WHERE o.species = @species AND o.qc_status = 'accepted' AND o.use_for_training AND NOT o.is_synthetic AND o.duplicate_of IS NULL
                ORDER BY o.user_id, st.stand_id, EXTRACT(YEAR FROM o.observed_on), o.submitted_at DESC
                """, new { species })).ToList();

            var ids = labels.Select(l => l.StandId).Distinct().ToArray();
            stands = ids.Length == 0
                ? []
                : (await db.QueryAsync<StandRow>(StandRow.SelectSql + " WHERE s.stand_id = ANY(@ids)", new { ids })).ToDictionary(s => s.StandId);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<TrainingPipeline.Row>();

        foreach (var label in labels)
        {
            if (TrainingPipeline.Label(label.Method, label.Outcome, label.EffortMinutes) is not { } y || !stands.TryGetValue(label.StandId, out var stand))
            {
                continue;
            }

            var input = stand.ToInput();
            var baseline = HabitatBaseline.Score(species, input, today).Score ?? 0.3;
            rows.Add(new TrainingPipeline.Row(TrainingPipeline.Features(input, stand.DistRoadM, baseline), y, label.TileId, label.Season, baseline, label.UserId));
        }

        ct.ThrowIfCancellationRequested();
        var report = TrainingPipeline.Run(rows);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var kind = report.Chosen is null ? "training_run" : report.Chosen.StartsWith("logistic") ? "logistic" : "random_forest";
        var version = $"habitat-{kind}-{stamp}";
        object parameters = new { features = TrainingPipeline.FeatureNames };

        if (report.Fitted is not null && report.Calibrator is not null)
        {
            var artifact = $"models/{species}/{version}.json";
            var json = report.Fitted switch
            {
                Ml.LogisticRegression lr => JsonSerializer.Serialize(lr),
                Ml.RandomForest rf => JsonSerializer.Serialize(rf),
                _ => throw new InvalidOperationException("Unknown model type"),
            };
            await writeArtifact(artifact, json);
            parameters = new { features = TrainingPipeline.FeatureNames, artifact, calibration = new { a = report.Calibrator.A, b = report.Calibrator.B } };
        }

        var status = report.Status == "candidate" && report.EligibleForPromotion ? "candidate" : report.Status;

        await using (var db = await openDb())
        {
            var id = await db.ExecuteScalarAsync<long>("""
                INSERT INTO model_versions (species, layer, kind, version, status, params, metrics, training_rows, notes, created_by)
                VALUES (@species, 'habitat', @kind, @version, @status, CAST(@params AS jsonb), CAST(@metrics AS jsonb), @rows, @notes, @actor)
                RETURNING id
                """, new
            {
                species, kind, version, status,
                @params = JsonSerializer.Serialize(parameters),
                metrics = JsonSerializer.Serialize(report),
                rows = report.Rows,
                notes = string.Join("; ", report.Gaps.Concat(report.Reasons)),
                actor,
            });
            await Audit.WriteAsync(db, actor, "model.train", $"model:{id}", new { species, status, report.Rows });
            progress.RecordsIn += rows.Count;
            progress.RecordsUpserted += 1;
            return id;
        }
    }

    // Promotion guard: only the heuristic baseline (a rollback target) or a run that passed validation
    public async Task<string?> PromoteAsync(long id, string? actor)
    {
        await using var db = await openDb();
        var row = await db.QuerySingleOrDefaultAsync<(string Species, string Kind, string Status, string Metrics)>(
            "SELECT species, kind, status, metrics::text FROM model_versions WHERE id = @id AND layer = 'habitat'", new { id });

        if (row.Species is null)
        {
            return "Model version not found";
        }

        if (row.Status == "production")
        {
            return "Already in production";
        }

        if (row.Kind != "heuristic")
        {
            var eligible = JsonDocument.Parse(row.Metrics).RootElement.TryGetProperty("EligibleForPromotion", out var e) && e.GetBoolean();
            if (!eligible || row.Status is "rejected" or "insufficient_data")
            {
                return "This run did not pass validation against the baseline, so it cannot go to production";
            }
        }

        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE model_versions SET status = 'retired' WHERE species = @Species AND layer = 'habitat' AND status = 'production'", new { row.Species }, tx);
        await db.ExecuteAsync("UPDATE model_versions SET status = 'production', promoted_at = now(), promoted_by = @actor WHERE id = @id", new { id, actor }, tx);
        await Audit.WriteAsync(db, actor, "model.promote", $"model:{id}", new { row.Species, row.Kind });
        await tx.CommitAsync();
        await onPromoted(id);
        return null;
    }

    public sealed record PresenceCheck(string Species, int PresenceStands, int BackgroundStands, double? Auc, string Caveat);

    // Presence-only sanity check against GBIF records: do stands with records score higher than a random
    // sample of all stands? This is not an accuracy measure, and the screen says so.
    public async Task<PresenceCheck> PresenceCheckAsync(string species)
    {
        await using var db = await openDb();
        var production = await db.ExecuteScalarAsync<long?>("SELECT id FROM model_versions WHERE species = @species AND layer = 'habitat' AND status = 'production'", new { species });
        var caveat = "Presence-only records are biased toward roads, paths and popular areas, and absence of a record is not absence of the berry. Use as a sanity check, never as accuracy.";

        if (production is null)
        {
            return new PresenceCheck(species, 0, 0, null, caveat);
        }

        var presence = (await db.QueryAsync<double>("""
            SELECT DISTINCT ON (p.stand_id) p.score FROM occurrence_records o
            JOIN forest_stands s ON ST_Intersects(s.geom, ST_Transform(o.geom, 3067))
            JOIN predictions p ON p.stand_id = s.stand_id AND p.model_version_id = @production AND p.species = @species
            WHERE o.species = @species AND COALESCE(o.coord_uncertainty_m, 0) <= 100 AND p.score IS NOT NULL
            """, new { species, production })).ToList();
        var background = (await db.QueryAsync<double>("""
            SELECT score FROM predictions WHERE model_version_id = @production AND species = @species AND score IS NOT NULL ORDER BY random() LIMIT 3000
            """, new { species, production })).ToList();

        if (presence.Count < 10 || background.Count < 100)
        {
            return new PresenceCheck(species, presence.Count, background.Count, null, caveat);
        }

        var scores = presence.Concat(background).ToList();
        var ys = presence.Select(_ => 1).Concat(background.Select(_ => 0)).ToList();
        return new PresenceCheck(species, presence.Count, background.Count, Ml.Metrics.Auc(scores, ys), caveat);
    }
}
