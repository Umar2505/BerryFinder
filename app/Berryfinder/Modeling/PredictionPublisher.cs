using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;

public sealed record StandRow(long StandId, string? LandClass, string? SiteMainClass, short? FertilityClass, short? SoilType, string? DrainageState,
    string? DevelopmentClass, short? MainSpecies, float? PropPine, float? PropSpruce, float? PropOther, float? MeanAge, float? BasalArea, float? MeanHeight,
    DateOnly? MeasurementDate, string? DataSourceCode, string? SoilClass, float? Ndvi, string? RestrictionSeverity, float? DistRoadM)
{
    public HabitatBaseline.StandInput ToInput() => new(LandClass, SiteMainClass, FertilityClass, SoilType, DrainageState, DevelopmentClass, MainSpecies,
        PropPine, PropSpruce, PropOther, MeanAge, BasalArea, MeanHeight, MeasurementDate, DataSourceCode, SoilClass, Ndvi, RestrictionSeverity);

    public const string SelectSql = """
        SELECT s.stand_id AS StandId, s.land_class AS LandClass, s.site_main_class AS SiteMainClass, s.fertility_class AS FertilityClass,
            s.soil_type AS SoilType, s.drainage_state AS DrainageState, s.development_class AS DevelopmentClass, s.main_species AS MainSpecies,
            s.prop_pine AS PropPine, s.prop_spruce AS PropSpruce, s.prop_other AS PropOther, s.mean_age AS MeanAge, s.basal_area AS BasalArea,
            s.mean_height AS MeanHeight, s.measurement_date AS MeasurementDate, s.data_source_code AS DataSourceCode, sf.soil_class AS SoilClass,
            sat.ndvi_median AS Ndvi, af.restriction_severity AS RestrictionSeverity, af.dist_road_m AS DistRoadM
        FROM forest_stands s
        LEFT JOIN soil_features sf ON sf.stand_id = s.stand_id
        LEFT JOIN access_features af ON af.stand_id = s.stand_id
        LEFT JOIN LATERAL (SELECT ndvi_median FROM satellite_features x WHERE x.stand_id = s.stand_id ORDER BY period DESC LIMIT 1) sat ON true
        """;
}

public sealed record ModelVersionRow(long Id, string Species, string Layer, string Kind, string Version, string Status, string Params, string Metrics,
    int TrainingRows, string? Notes, DateTime CreatedAt, DateTime? PromotedAt, string? PromotedBy);

// Turns the production habitat model of each species into stored per-stand predictions and merged
// picking areas. Predictions are kept per model version, so rolling back is switching the production
// flag, not recomputing.
public sealed class PredictionPublisher(OpenDb openDb, Func<string, Task<string>> readArtifact)
{
    // Production model per species, read by the tile endpoint and the map on every request
    public IReadOnlyDictionary<string, long> Production { get; private set; } = new Dictionary<string, long>();

    // Kind of each production model ("heuristic", "logistic", "pytorch_ensemble", ...), for the wording on screen
    public IReadOnlyDictionary<string, string> ProductionKinds { get; private set; } = new Dictionary<string, string>();

    public event Action? Published;

    public async Task RefreshProductionAsync()
    {
        await using var db = await openDb();
        var rows = (await db.QueryAsync<(string Species, long Id, string Kind)>("SELECT species, id, kind FROM model_versions WHERE layer = 'habitat' AND status = 'production'")).ToList();
        Production = rows.ToDictionary(r => r.Species, r => r.Id);
        ProductionKinds = rows.ToDictionary(r => r.Species, r => r.Kind);
    }

    public async Task<long> EnsureBaselineAsync(string species)
    {
        await using var db = await openDb();
        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO model_versions (species, layer, kind, version, status, params, notes, created_by)
            VALUES (@species, 'habitat', 'heuristic', @version, 'candidate', CAST(@params AS jsonb), @notes, 'system')
            ON CONFLICT (species, layer, version) DO UPDATE SET params = EXCLUDED.params
            RETURNING id
            """, new
        {
            species,
            version = HabitatBaseline.Version,
            @params = JsonSerializer.Serialize(new { description = "Literature-grounded ecological baseline; response shapes are expert judgement, not fitted" }),
            notes = "Unvalidated ecological estimate",
        });

        // The baseline is production until a validated model is promoted over it
        await db.ExecuteAsync("""
            UPDATE model_versions SET status = 'production', promoted_at = now(), promoted_by = 'system'
            WHERE id = @id AND NOT EXISTS (SELECT 1 FROM model_versions WHERE species = @species AND layer = 'habitat' AND status = 'production')
            """, new { id, species });
        return id;
    }

    public void NotifyPublished() => Published?.Invoke();

    // Every berry with a production model: the stand baselines, plus any berry the grid model covers
    public async Task PublishAllAsync(JobProgress progress, CancellationToken ct)
    {
        foreach (var sp in Species.Supported)
        {
            await EnsureBaselineAsync(sp);
        }

        await RefreshProductionAsync();
        var targets = Production.OrderBy(p => p.Key).ToList();

        for (var i = 0; i < targets.Count; i++)
        {
            var (sp, id) = targets[i];
            var k = i;
            await PublishAsync(sp, id, new JobProgress((f, m) => progress.ReportAsync((k + f) / targets.Count, $"{sp}: {m}")), ct);
        }

        progress.RecordsUpserted += 1;
        Published?.Invoke();
    }

    public async Task PublishAsync(string species, long modelVersionId, JobProgress progress, CancellationToken ct)
    {
        ModelVersionRow model;
        await using (var db = await openDb())
        {
            model = await db.QuerySingleAsync<ModelVersionRow>("""
                SELECT id, species, layer, kind, version, status, params::text AS Params, metrics::text AS Metrics, training_rows AS TrainingRows, notes,
                    created_at AS CreatedAt, promoted_at AS PromotedAt, promoted_by AS PromotedBy
                FROM model_versions WHERE id = @modelVersionId
                """, new { modelVersionId });

            // The grid model has no per-stand predictions: its picking areas come straight from the scored cells
            if (model.Kind == ModelGate.Kind)
            {
                await progress.ReportAsync(0.2, "merging grid cells into picking areas");
                await BuildGridAreasAsync(species, model, ct);
                await progress.ReportAsync(1, "published");
                return;
            }

            await db.ExecuteAsync("DELETE FROM predictions WHERE model_version_id = @modelVersionId AND species = @species", new { modelVersionId, species });
        }

        var trained = model.Kind == "heuristic" ? null : await TrainedModel.LoadAsync(model, readArtifact);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        long lastId = 0;
        var done = 0;
        int total;

        await using (var db = await openDb())
        {
            total = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM forest_stands");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<StandRow> page;
            await using (var db = await openDb())
            {
                page = (await db.QueryAsync<StandRow>(StandRow.SelectSql + " WHERE s.stand_id > @lastId ORDER BY s.stand_id LIMIT 4000", new { lastId })).ToList();
            }

            if (page.Count == 0)
            {
                break;
            }

            var rows = new JsonArray();
            foreach (var stand in page)
            {
                var input = stand.ToInput();
                var baseline = HabitatBaseline.Score(species, input, today);
                double? probability = null;
                var suitability = baseline.Suitability;
                var score = baseline.Score;

                if (trained is not null && baseline.Score is { } b)
                {
                    probability = trained.Predict(TrainingPipeline.Features(input, stand.DistRoadM, b));
                    score = probability;
                    suitability = probability >= 0.7 ? "high" : probability >= 0.5 ? "moderate" : probability >= 0.3 ? "low" : "unlikely";
                }

                rows.Add(new JsonObject
                {
                    ["id"] = stand.StandId,
                    ["score"] = score,
                    ["p"] = probability,
                    ["s"] = suitability,
                    ["e"] = baseline.Evidence,
                    ["x"] = baseline.Excluded,
                    ["d"] = JsonSerializer.SerializeToNode(baseline.Drivers.Take(6).Select(d => new { f = d.Factor, effect = d.Effect, en = d.En, fi = d.Fi })),
                });
            }

            await using (var db = await openDb())
            {
                progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO predictions (stand_id, species, model_version_id, prediction_kind, score, probability, suitability, evidence, drivers, excluded)
                    SELECT (x->>'id')::bigint, @species, @mv, @kind, (x->>'score')::real, (x->>'p')::real, x->>'s', x->>'e', x->'d', (x->>'x')::boolean
                    FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS x
                    """, new { rows = rows.ToJsonString(), species, mv = modelVersionId, kind = trained is null ? "heuristic" : "trained" }, commandTimeout: 300, cancellationToken: ct));
            }

            done += page.Count;
            lastId = page[^1].StandId;
            await progress.ReportAsync(total == 0 ? 1 : 0.85 * done / total, $"{done}/{total} stands scored");
        }

        await progress.ReportAsync(0.9, "merging picking areas");
        await BuildPickingAreasAsync(species, modelVersionId, ct);
        await progress.ReportAsync(1, "published");
    }

    // Contiguous high/moderate stands, grouped inside 1 km cells so an area stays walkable, become picking areas
    private async Task BuildPickingAreasAsync(string species, long modelVersionId, CancellationToken ct)
    {
        await using var db = await openDb();
        await db.ExecuteAsync("DELETE FROM picking_areas WHERE model_version_id = @modelVersionId AND species = @species", new { modelVersionId, species });
        await db.ExecuteAsync(new CommandDefinition("""
            WITH good AS (
                SELECT s.stand_id, s.geom, p.score, p.suitability,
                    floor(ST_X(ST_PointOnSurface(s.geom)) / 1000) AS cx, floor(ST_Y(ST_PointOnSurface(s.geom)) / 1000) AS cy
                FROM predictions p JOIN forest_stands s ON s.stand_id = p.stand_id
                WHERE p.model_version_id = @modelVersionId AND p.species = @species AND p.suitability IN ('high', 'moderate') AND NOT p.excluded
            ), clustered AS (
                SELECT *, ST_ClusterDBSCAN(geom, eps := 20, minpoints := 1) OVER (PARTITION BY cx, cy) AS cid FROM good
            ), agg AS (
                SELECT cx, cy, cid,
                    ST_Multi(ST_CollectionExtract(ST_MakeValid(ST_UnaryUnion(ST_Collect(ST_Buffer(geom, 1)))), 3)) AS geom,
                    sum(ST_Area(geom)) / 10000 AS area_ha, count(*) AS n,
                    sum(score * ST_Area(geom)) / NULLIF(sum(ST_Area(geom)), 0) AS mean_score,
                    sum(CASE WHEN suitability = 'high' THEN ST_Area(geom) ELSE 0 END) / NULLIF(sum(ST_Area(geom)), 0) AS high_share
                FROM clustered GROUP BY cx, cy, cid
            )
            INSERT INTO picking_areas (species, model_version_id, suitability, mean_score, area_ha, stand_count, high_share, dist_road_m, access_point,
                restriction, geom, geom_web, centroid)
            SELECT @species, @modelVersionId, CASE WHEN high_share >= 0.5 THEN 'high' ELSE 'moderate' END, mean_score, area_ha, n, high_share,
                ap.d, ap.point, rs.name, a.geom, ST_Multi(ST_Transform(ST_SimplifyPreserveTopology(a.geom, 4), 3857)), ST_Transform(ST_PointOnSurface(a.geom), 4326)
            FROM agg a
            LEFT JOIN LATERAL (SELECT ST_Distance(w.geom, a.geom) AS d, ST_Transform(ST_ClosestPoint(w.geom, a.geom), 4326) AS point FROM access_ways w
                WHERE w.kind IN ('road', 'track') AND COALESCE(w.access_tag, '') NOT IN ('private', 'no') AND ST_DWithin(w.geom, a.geom, 10000)
                ORDER BY w.geom <-> a.geom LIMIT 1) ap ON true
            LEFT JOIN LATERAL (SELECT r.name FROM access_restrictions r WHERE r.active AND ST_Intersects(r.geom, a.geom) LIMIT 1) rs ON true
            WHERE a.area_ha >= 1.5
            """, new { species, modelVersionId }, commandTimeout: 900, cancellationToken: ct));
    }

    // The grid model's best cells for this berry (ModelGate.Select: its habitat, top of the Nordic background, the region's best),
    // merged inside 1 km blocks like the stand areas. What the model saw (land cover, soil, stand data) is averaged over the cells
    // and kept with the area, since no stands lie behind it.
    private async Task BuildGridAreasAsync(string species, ModelVersionRow model, CancellationToken ct)
    {
        var run = JsonNode.Parse(model.Params)!["run_version"]!.GetValue<string>();

        await using var db = await openDb();
        var cells = (await db.QueryAsync<(int X, int Y, bool Ood, string Scores, string Habitat)>(
                "SELECT cell_x, cell_y, ood, scores::text, habitat::text FROM model_grid_cells WHERE run_version = @run", new { run }))
            .Select(c => GridCell.FromJson(c.X, c.Y, c.Ood, c.Scores, c.Habitat))
            .ToList();
        var picked = ModelGate.Select(cells, species);
        var rows = JsonSerializer.Serialize(picked.Select(c => new { x = c.X, y = c.Y, high = c.High, mire = c.Mire ? 1 : 0 }));

        await db.ExecuteAsync("DELETE FROM picking_areas WHERE model_version_id = @id AND species = @species", new { id = model.Id, species });
        await db.ExecuteAsync(new CommandDefinition("""
            WITH good AS (
                SELECT c.geom, c.habitat || jsonb_build_object('mire', (j->>'mire')::int) AS habitat, (c.scores -> @species ->> 'idx')::float8 AS idx, (c.scores -> @species ->> 'sd')::float8 AS sd,
                    (j->>'high')::boolean AS high, floor(c.cell_x / 1000.0) AS cx, floor(c.cell_y / 1000.0) AS cy
                FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS j
                JOIN model_grid_cells c ON c.run_version = @run AND c.cell_x = (j->>'x')::int AND c.cell_y = (j->>'y')::int
            ), clustered AS (
                SELECT *, ST_ClusterDBSCAN(geom, eps := 1, minpoints := 1) OVER (PARTITION BY cx, cy) AS cid FROM good
            ), agg AS (
                SELECT cx, cy, cid, ST_Multi(ST_UnaryUnion(ST_Collect(geom))) AS geom, sum(ST_Area(geom)) / 10000 AS area_ha, count(*) AS n,
                    avg(idx) AS mean_idx, avg(sd) AS mean_sd, avg(CASE WHEN high THEN 1.0 ELSE 0.0 END) AS high_share, jsonb_agg(habitat) AS habitats
                FROM clustered GROUP BY cx, cy, cid
            )
            INSERT INTO picking_areas (species, model_version_id, suitability, mean_score, area_ha, stand_count, high_share, dist_road_m, access_point,
                restriction, geom, geom_web, centroid, details)
            SELECT @species, @id, CASE WHEN a.high_share >= 0.5 THEN 'high' ELSE 'moderate' END, a.mean_idx / 100, a.area_ha, a.n, a.high_share,
                ap.d, ap.point, rs.name, a.geom, ST_Multi(ST_Transform(a.geom, 3857)), ST_Transform(ST_PointOnSurface(a.geom), 4326),
                jsonb_build_object('source', 'grid_model', 'run', @run, 'cells', a.n, 'mean_index', round(a.mean_idx::numeric, 1),
                    'uncertainty', round(a.mean_sd::numeric, 3), 'evidence', @evidence, 'habitat', COALESCE(hab.habitat, '{}'::jsonb))
            FROM agg a
            LEFT JOIN LATERAL (
                SELECT jsonb_object_agg(e.key, e.v) AS habitat FROM (
                    SELECT kv.key, round(avg((kv.value #>> '{}')::numeric), 3) AS v
                    FROM jsonb_array_elements(a.habitats) AS h, jsonb_each(h) AS kv
                    WHERE jsonb_typeof(kv.value) = 'number' GROUP BY kv.key) e
            ) hab ON true
            LEFT JOIN LATERAL (SELECT ST_Distance(w.geom, a.geom) AS d, ST_Transform(ST_ClosestPoint(w.geom, a.geom), 4326) AS point FROM access_ways w
                WHERE w.kind IN ('road', 'track') AND COALESCE(w.access_tag, '') NOT IN ('private', 'no') AND ST_DWithin(w.geom, a.geom, 10000)
                ORDER BY w.geom <-> a.geom LIMIT 1) ap ON true
            LEFT JOIN LATERAL (SELECT r.name FROM access_restrictions r WHERE r.active AND ST_Intersects(r.geom, a.geom) LIMIT 1) rs ON true
            """, new { species, id = model.Id, run, rows, evidence = ModelGate.Evidence }, commandTimeout: 900, cancellationToken: ct));
    }
}

// A trained habitat model rebuilt from the registry: the coefficients or trees, plus its calibrator
public sealed class TrainedModel(Ml.IModel model, Ml.Calibrator calibrator)
{
    public double Predict(double[] features) => calibrator.Apply(model.PredictProbability(features));

    public static async Task<TrainedModel> LoadAsync(ModelVersionRow row, Func<string, Task<string>> readArtifact)
    {
        var p = JsonNode.Parse(row.Params)!;
        var calibrator = new Ml.Calibrator(p["calibration"]!["a"]!.GetValue<double>(), p["calibration"]!["b"]!.GetValue<double>());
        var artifact = await readArtifact(p["artifact"]!.GetValue<string>());

        Ml.IModel model = row.Kind switch
        {
            "logistic" => JsonSerializer.Deserialize<Ml.LogisticRegression>(artifact)!,
            "random_forest" => JsonSerializer.Deserialize<Ml.RandomForest>(artifact)!,
            _ => throw new InvalidOperationException($"Unknown model kind {row.Kind}"),
        };

        return new TrainedModel(model, calibrator);
    }
}
