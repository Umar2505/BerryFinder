using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;

public static class JobKinds
{
    public const string Stands = "stands";
    public const string Soil = "soil";
    public const string Access = "access";
    public const string Protected = "protected";
    public const string Features = "features";
    public const string Weather = "weather";
    public const string GridWeather = "gridweather";
    public const string Scenes = "scenes";
    public const string Gbif = "gbif";
    public const string Publish = "publish";
    public const string Pipeline = "pipeline";
    public const string Train = "train";
    public const string ModelGrid = "model_grid";

    public static readonly string[] All = [Pipeline, Stands, Soil, Access, Protected, Features, Weather, GridWeather, Scenes, Gbif, Publish, ModelGrid];

    public static string SourceOf(string kind) => kind switch
    {
        Stands => "metsakeskus_stands",
        Soil => "gtk_soil",
        Access => "osm_access",
        Protected => "syke_protected",
        Weather => "fmi_weather",
        GridWeather => "nasa_power",
        Scenes => "copernicus_s2",
        Gbif => "gbif_occurrences",
        Train => "field_observations",
        ModelGrid => "nordic_habitat_model",
        _ => "metsakeskus_stands",
    };
}

public sealed record JobRow(long Id, string SourceId, string Kind, string Params, string Status, string? RequestedBy, DateTime CreatedAt,
    DateTime? StartedAt, DateTime? FinishedAt, float Progress, int RecordsIn, int RecordsUpserted, string? Message, string? Error);

// Runs ingestion and publishing jobs one at a time from the ingestion_jobs table. Jobs survive a
// restart: an interrupted job goes back to the queue and per-tile timestamps let it skip finished work.
public sealed class JobRunner(OpenDb openDb, Func<TimeSpan, string, Task<IAsyncDisposable>> holdBackgroundWork, PredictionPublisher publisher, TrainingService trainer,
    GridModelImporter gridModel)
{
    public event Action? Changed;

    public async Task<long> EnqueueAsync(string kind, object? parameters, string? requestedBy)
    {
        await using var db = await openDb();

        // One queued copy of a kind is enough; a second request joins the first
        var existing = await db.ExecuteScalarAsync<long?>("SELECT id FROM ingestion_jobs WHERE kind = @kind AND status = 'queued' AND params = CAST(@p AS jsonb) LIMIT 1",
            new { kind, p = JsonSerializer.Serialize(parameters ?? new { }) });
        if (existing is { } id)
        {
            return id;
        }

        id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO ingestion_jobs (source_id, kind, params, requested_by) VALUES (@source, @kind, CAST(@p AS jsonb), @by) RETURNING id
            """, new { source = JobKinds.SourceOf(kind), kind, p = JsonSerializer.Serialize(parameters ?? new { }), by = requestedBy });
        await Audit.WriteAsync(db, requestedBy, "job.enqueue", $"job:{id}", new { kind, parameters });
        Changed?.Invoke();
        return id;
    }

    public async Task CancelAsync(long id, string? actor)
    {
        await using var db = await openDb();
        await db.ExecuteAsync("UPDATE ingestion_jobs SET status = 'cancelled', finished_at = now() WHERE id = @id AND status = 'queued'", new { id });
        await Audit.WriteAsync(db, actor, "job.cancel", $"job:{id}", new { });
        Changed?.Invoke();
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        await using (var db = await openDb())
        {
            await db.ExecuteAsync("UPDATE ingestion_jobs SET status = 'queued', message = 'Re-queued after a restart' WHERE status = 'running'");
        }

        while (!ct.IsCancellationRequested)
        {
            JobRow? job;
            try
            {
                await using var db = await openDb();
                job = await db.QuerySingleOrDefaultAsync<JobRow>("""
                    UPDATE ingestion_jobs SET status = 'running', started_at = now(), progress = 0, error = NULL
                    WHERE id = (SELECT id FROM ingestion_jobs WHERE status = 'queued' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
                    RETURNING id, source_id AS SourceId, kind, params::text AS Params, status, requested_by AS RequestedBy, created_at AS CreatedAt,
                        started_at AS StartedAt, finished_at AS FinishedAt, progress, records_in AS RecordsIn, records_upserted AS RecordsUpserted, message, error
                    """);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Instance.Warning($"Job queue unavailable: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                continue;
            }

            Changed?.Invoke();
            await RunAsync(job, ct);
            Changed?.Invoke();
        }
    }

    private async Task RunAsync(JobRow job, CancellationToken ct)
    {
        await using var hold = await holdBackgroundWork(TimeSpan.FromHours(2), $"job {job.Kind}");
        var lastWrite = DateTime.MinValue;
        var progress = new JobProgress(async (fraction, message) =>
        {
            // Throttle progress writes; the admin screen refreshes from them
            if ((DateTime.UtcNow - lastWrite).TotalSeconds < 2 && fraction < 1)
            {
                return;
            }

            lastWrite = DateTime.UtcNow;
            await using var db = await openDb();
            await db.ExecuteAsync("UPDATE ingestion_jobs SET progress = @fraction, message = @message WHERE id = @id", new { fraction, message, id = job.Id });
            Changed?.Invoke();
        });

        var sources = new HashSet<string> { job.SourceId };

        try
        {
            var parameters = JsonNode.Parse(job.Params)?.AsObject() ?? [];
            var onlyMissing = parameters["onlyMissing"]?.GetValue<bool>() ?? false;
            await MarkSourcesAsync(sources, attempt: true);

            switch (job.Kind)
            {
                case JobKinds.Pipeline:
                    var steps = new[] { JobKinds.Stands, JobKinds.Soil, JobKinds.Access, JobKinds.Protected, JobKinds.Features, JobKinds.Weather, JobKinds.GridWeather, JobKinds.Scenes, JobKinds.Gbif, JobKinds.Publish };
                    for (var i = 0; i < steps.Length; i++)
                    {
                        var step = steps[i];
                        var source = JobKinds.SourceOf(step);
                        sources.Add(source);
                        await MarkSourcesAsync([source], attempt: true);
                        var stepProgress = new JobProgress((f, m) => progress.ReportAsync((i + f) / steps.Length, $"{step}: {m}"));
                        try
                        {
                            await RunStepAsync(step, onlyMissing, stepProgress, ct);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested && step is JobKinds.Protected or JobKinds.Weather or JobKinds.GridWeather or JobKinds.Scenes or JobKinds.Gbif)
                        {
                            // Supporting sources must not block publishing predictions from the core forest, soil and access data
                            Log.Instance.Warning($"Optional step {step} failed: {ex.Message}");
                            stepProgress.Warnings.Add($"{step} failed ({ex.Message}); run it again from the dashboard");
                        }
                        progress.RecordsIn += stepProgress.RecordsIn;
                        progress.RecordsUpserted += stepProgress.RecordsUpserted;
                        progress.Warnings.AddRange(stepProgress.Warnings);
                        await MarkSourcesAsync([source], attempt: false);
                    }
                    break;

                case JobKinds.Train:
                    await trainer.TrainAllAsync(job.RequestedBy, progress, ct);
                    break;

                case JobKinds.ModelGrid:
                    await gridModel.ImportAsync(progress, ct);
                    break;

                default:
                    await RunStepAsync(job.Kind, onlyMissing, progress, ct);
                    break;
            }

            await MarkSourcesAsync(sources, attempt: false);
            await using var db = await openDb();
            await db.ExecuteAsync("""
                UPDATE ingestion_jobs SET status = 'succeeded', finished_at = now(), progress = 1, records_in = @RecordsIn, records_upserted = @RecordsUpserted,
                    message = @message WHERE id = @Id
                """, new { progress.RecordsIn, progress.RecordsUpserted, message = progress.Warnings.Count > 0 ? string.Join("; ", progress.Warnings.Distinct()) : "Completed", job.Id });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The instance is stopping; the next start re-queues this job
        }
        catch (Exception ex)
        {
            Log.Instance.Error($"Job {job.Id} ({job.Kind}) failed: {ex}");
            await using var db = await openDb();
            await db.ExecuteAsync("""
                UPDATE ingestion_jobs SET status = 'failed', finished_at = now(), records_in = @RecordsIn, records_upserted = @RecordsUpserted, error = @error WHERE id = @Id
                """, new { progress.RecordsIn, progress.RecordsUpserted, error = ex.Message, job.Id });
        }
    }

    private async Task RunStepAsync(string kind, bool onlyMissing, JobProgress progress, CancellationToken ct)
    {
        var tiles = await LoadTilesAsync(kind, onlyMissing);
        var extent = await PilotExtentAsync();

        switch (kind)
        {
            case JobKinds.Stands:
                await ForEachTileAsync(tiles, progress, ct, t => MetsakeskusConnector.IngestTileAsync(openDb, t, progress, ct));
                break;
            case JobKinds.Soil:
                await ForEachTileAsync(tiles, progress, ct, t => GtkSoilConnector.IngestTileAsync(openDb, t, progress, ct));
                break;
            case JobKinds.Access:
                await ForEachTileAsync(tiles, progress, ct, t => OsmAccessConnector.IngestTileAsync(openDb, t, progress, ct));
                break;
            case JobKinds.Protected:
                await progress.ReportAsync(0.1, "Fetching protected areas");
                await SykeProtectedConnector.IngestAsync(openDb, (extent.XMin, extent.YMin, extent.XMax, extent.YMax), progress, ct);
                break;
            case JobKinds.Features:
                await ForEachTileAsync(tiles, progress, ct, t => FeatureBuilder.BuildTileAsync(openDb, t, progress, ct));
                break;
            case JobKinds.Weather:
                await progress.ReportAsync(0.1, "Fetching FMI station observations");
                await FmiWeatherConnector.IngestAsync(openDb, 6, progress, ct);
                break;
            case JobKinds.GridWeather:
                await progress.ReportAsync(0.1, "Fetching NASA POWER grid data");
                await NasaPowerConnector.IngestAsync(openDb, (extent.South + extent.North) / 2, (extent.West + extent.East) / 2, 6, progress, ct);
                break;
            case JobKinds.Scenes:
                await progress.ReportAsync(0.1, "Searching the Sentinel-2 catalogue");
                await SentinelStacConnector.IngestAsync(openDb, (extent.West, extent.South, extent.East, extent.North), 2, progress, ct);
                break;
            case JobKinds.Gbif:
                foreach (var sp in Registry.SpeciesRows.Where(s => s.Supported && s.GbifTaxon is not null))
                {
                    await progress.ReportAsync(0.2, $"Fetching GBIF records for {sp.NameEn}");
                    await GbifConnector.IngestAsync(openDb, sp.Id, sp.GbifTaxon!.Value, (extent.West, extent.South, extent.East, extent.North), progress, ct);
                }
                break;
            case JobKinds.Publish:
                await publisher.PublishAllAsync(progress, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown job kind '{kind}'");
        }
    }

    private static async Task ForEachTileAsync(IReadOnlyList<PilotTile> tiles, JobProgress progress, CancellationToken ct, Func<PilotTile, Task> work)
    {
        for (var i = 0; i < tiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await progress.ReportAsync((double)i / tiles.Count, $"tile {tiles[i].Id} ({i + 1}/{tiles.Count})");

            try
            {
                await work(tiles[i]);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The tile keeps no "ingested" timestamp, so the next run with onlyMissing retries just this one
                progress.Warnings.Add($"tile {tiles[i].Id} failed ({ex.Message}); it will be retried on the next run");
            }
        }

        await progress.ReportAsync(1, $"{tiles.Count} tiles");
    }

    private async Task<IReadOnlyList<PilotTile>> LoadTilesAsync(string kind, bool onlyMissing)
    {
        var column = kind switch
        {
            JobKinds.Stands => "stands_ingested_at",
            JobKinds.Soil => "soil_ingested_at",
            JobKinds.Access => "access_ingested_at",
            JobKinds.Features => "features_built_at",
            _ => null,
        };

        await using var db = await openDb();
        var filter = onlyMissing && column is not null ? $" AND {column} IS NULL" : "";
        return (await db.QueryAsync<PilotTile>($"""
            SELECT id, x_min AS XMin, y_min AS YMin, x_max AS XMax, y_max AS YMax,
                ST_XMin(ST_Transform(geom, 4326)) AS West, ST_YMin(ST_Transform(geom, 4326)) AS South,
                ST_XMax(ST_Transform(geom, 4326)) AS East, ST_YMax(ST_Transform(geom, 4326)) AS North
            FROM pilot_tiles WHERE enabled{filter} ORDER BY id
            """)).ToList();
    }

    private async Task<(int XMin, int YMin, int XMax, int YMax, double West, double South, double East, double North)> PilotExtentAsync()
    {
        await using var db = await openDb();
        var e = await db.QuerySingleAsync("""
            SELECT min(x_min) AS xmin, min(y_min) AS ymin, max(x_max) AS xmax, max(y_max) AS ymax FROM pilot_tiles WHERE enabled
            """);
        var b = await db.QuerySingleAsync("""
            SELECT ST_XMin(g) AS w, ST_YMin(g) AS s, ST_XMax(g) AS e, ST_YMax(g) AS n
            FROM (SELECT ST_Transform(ST_SetSRID(ST_Extent(geom)::geometry, 3067), 4326) AS g FROM pilot_tiles WHERE enabled) t
            """);
        return ((int)e.xmin, (int)e.ymin, (int)e.xmax, (int)e.ymax, (double)b.w, (double)b.s, (double)b.e, (double)b.n);
    }

    private async Task MarkSourcesAsync(IEnumerable<string> sources, bool attempt)
    {
        await using var db = await openDb();
        foreach (var source in sources)
        {
            if (attempt)
            {
                await db.ExecuteAsync("UPDATE data_sources SET last_attempt_at = now() WHERE id = @source", new { source });
            }
            else
            {
                await db.ExecuteAsync($"""
                    UPDATE data_sources SET last_success_at = now(), records = ({RecordCountSql(source)}) WHERE id = @source
                    """, new { source });
            }
        }
    }

    private static string RecordCountSql(string source) => source switch
    {
        "metsakeskus_stands" => "SELECT count(*) FROM forest_stands",
        "gtk_soil" => "SELECT count(*) FROM soil_polygons",
        "osm_access" => "SELECT count(*) FROM access_ways",
        "syke_protected" => "SELECT count(*) FROM access_restrictions WHERE source = 'syke'",
        "fmi_weather" => "SELECT count(*) FROM weather_daily",
        "nasa_power" => "SELECT count(*) FROM weather_grid_daily",
        "copernicus_s2" => "SELECT count(*) FROM satellite_scenes",
        "gbif_occurrences" => "SELECT count(*) FROM occurrence_records",
        "field_observations" => "SELECT count(*) FROM field_observations WHERE NOT is_synthetic",
        _ => "SELECT 0",
    };
}

// Spatial joins that turn raw layers into per-stand features
public static class FeatureBuilder
{
    public static async Task BuildTileAsync(OpenDb openDb, PilotTile tile, JobProgress progress, CancellationToken ct)
    {
        await using var db = await openDb();

        // Finest-scale GTK polygon under a point guaranteed to lie inside the stand
        var soil = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO soil_features (stand_id, soil_class, soil_label, source_layer, updated_at)
            SELECT s.stand_id, sp.soil_class, sp.surface_label, sp.source_layer, now()
            FROM forest_stands s
            CROSS JOIN LATERAL (
                SELECT soil_class, surface_label, source_layer FROM soil_polygons p
                WHERE ST_Intersects(p.geom, ST_PointOnSurface(s.geom)) ORDER BY scale_rank LIMIT 1) sp
            WHERE s.tile_id = @tile
            ON CONFLICT (stand_id) DO UPDATE SET soil_class = EXCLUDED.soil_class, soil_label = EXCLUDED.soil_label,
                source_layer = EXCLUDED.source_layer, updated_at = now()
            """, new { tile = tile.Id }, commandTimeout: 600, cancellationToken: ct));

        // Straight-line distance from the stand edge to the nearest way of each kind, the nearest drivable
        // access point for navigation, and any protected area the stand overlaps
        var access = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO access_features (stand_id, dist_road_m, dist_track_m, dist_path_m, access_point, access_kind, restriction_id, restriction_severity, updated_at)
            SELECT s.stand_id, r.d, t.d, p.d, ap.point, ap.kind, rs.id, rs.severity, now()
            FROM forest_stands s
            LEFT JOIN LATERAL (SELECT ST_Distance(w.geom, s.geom) AS d FROM access_ways w
                WHERE w.kind = 'road' AND COALESCE(w.access_tag, '') NOT IN ('private', 'no') AND ST_DWithin(w.geom, s.geom, 10000)
                ORDER BY w.geom <-> s.geom LIMIT 1) r ON true
            LEFT JOIN LATERAL (SELECT ST_Distance(w.geom, s.geom) AS d FROM access_ways w
                WHERE w.kind = 'track' AND ST_DWithin(w.geom, s.geom, 10000) ORDER BY w.geom <-> s.geom LIMIT 1) t ON true
            LEFT JOIN LATERAL (SELECT ST_Distance(w.geom, s.geom) AS d FROM access_ways w
                WHERE w.kind = 'path' AND ST_DWithin(w.geom, s.geom, 10000) ORDER BY w.geom <-> s.geom LIMIT 1) p ON true
            LEFT JOIN LATERAL (SELECT ST_Transform(ST_ClosestPoint(w.geom, s.geom), 4326) AS point, w.kind FROM access_ways w
                WHERE w.kind IN ('road', 'track') AND COALESCE(w.access_tag, '') NOT IN ('private', 'no') AND ST_DWithin(w.geom, s.geom, 10000)
                ORDER BY w.geom <-> s.geom LIMIT 1) ap ON true
            LEFT JOIN LATERAL (SELECT a.id, a.severity FROM access_restrictions a
                WHERE a.active AND ST_Intersects(a.geom, s.geom) ORDER BY (a.severity = 'exclude') DESC LIMIT 1) rs ON true
            WHERE s.tile_id = @tile
            ON CONFLICT (stand_id) DO UPDATE SET dist_road_m = EXCLUDED.dist_road_m, dist_track_m = EXCLUDED.dist_track_m, dist_path_m = EXCLUDED.dist_path_m,
                access_point = EXCLUDED.access_point, access_kind = EXCLUDED.access_kind, restriction_id = EXCLUDED.restriction_id,
                restriction_severity = EXCLUDED.restriction_severity, updated_at = now()
            """, new { tile = tile.Id }, commandTimeout: 900, cancellationToken: ct));

        await db.ExecuteAsync("UPDATE pilot_tiles SET features_built_at = now() WHERE id = @id", new { id = tile.Id });
        progress.RecordsIn += soil;
        progress.RecordsUpserted += soil + access;
    }

    // Refreshes only the restriction columns, after an admin adds or lifts a restriction
    public static async Task RefreshRestrictionsAsync(OpenDb openDb)
    {
        await using var db = await openDb();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE access_features af SET restriction_id = rs.id, restriction_severity = rs.severity, updated_at = now()
            FROM forest_stands s
            LEFT JOIN LATERAL (SELECT a.id, a.severity FROM access_restrictions a
                WHERE a.active AND ST_Intersects(a.geom, s.geom) ORDER BY (a.severity = 'exclude') DESC LIMIT 1) rs ON true
            WHERE af.stand_id = s.stand_id AND (af.restriction_id IS DISTINCT FROM rs.id OR af.restriction_severity IS DISTINCT FROM rs.severity)
            """, commandTimeout: 900));
    }
}

public static class Audit
{
    public static Task WriteAsync(System.Data.Common.DbConnection db, string? actor, string action, string? target, object details) =>
        db.ExecuteAsync("INSERT INTO audit_logs (actor, action, target, details) VALUES (@actor, @action, @target, CAST(@details AS jsonb))",
            new { actor, action, target, details = JsonSerializer.Serialize(details) });
}
