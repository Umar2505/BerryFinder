using Dapper;

public sealed record CoverageStats(long Stands, int Tiles, int TilesWithStands, int TilesFeatures, double SoilShare, double AccessShare, long RestrictedStands, long ProtectedAreas,
    long AccessWays, long WeatherStationDays, long GridDays, long Scenes, DateOnly? LatestScene, long Occurrences, int? OldestMeasurementYear, int? NewestMeasurementYear,
    DateTime? LastStandIngest);

public sealed record SourceRow(string Id, string Name, string Provider, string License, string CommercialUse, string Status, string? Coverage, string? Notes, long Records,
    DateTime? LastAttemptAt, DateTime? LastSuccessAt, string Endpoint);

public sealed record LabelRow2(string Species, string Qc, string Outcome, string Method, long N);

public sealed record AreaPrediction(string Species, string Kind, string Suitability, long Stands);

public sealed record ModelRow(long Id, string Species, string Kind, string Version, string Status, int TrainingRows, string Notes, string Metrics, DateTime CreatedAt, DateTime? PromotedAt, string? PromotedBy);

public sealed record RestrictionRow(long Id, string Source, string? Name, string RestrictionType, string Severity, string? Notes, bool Active, DateTime CreatedAt, string? CreatedBy);

public sealed record AuditRow(DateTime At, string? Actor, string Action, string? Target, string Details);

public sealed record Engagement(long Events7d, long Users7d, long Searches7d, long AreasOpened7d, long TripsPlanned7d, long Navigations7d, long Reports7d, long Checkouts7d, long Accounts, long Premium, long Professional);

public sealed class AdminQueries(OpenDb openDb)
{
    public async Task<CoverageStats> CoverageAsync()
    {
        await using var db = await openDb();
        return await db.QuerySingleAsync<CoverageStats>("""
            SELECT (SELECT count(*) FROM forest_stands) AS Stands,
                (SELECT count(*)::int FROM pilot_tiles WHERE enabled) AS Tiles,
                (SELECT count(*)::int FROM pilot_tiles WHERE enabled AND stands_ingested_at IS NOT NULL) AS TilesWithStands,
                (SELECT count(*)::int FROM pilot_tiles WHERE enabled AND features_built_at IS NOT NULL) AS TilesFeatures,
                COALESCE((SELECT count(*)::float8 FROM soil_features) / NULLIF((SELECT count(*) FROM forest_stands), 0), 0) AS SoilShare,
                COALESCE((SELECT count(*)::float8 FROM access_features WHERE dist_road_m IS NOT NULL) / NULLIF((SELECT count(*) FROM forest_stands), 0), 0) AS AccessShare,
                (SELECT count(*) FROM access_features WHERE restriction_id IS NOT NULL) AS RestrictedStands,
                (SELECT count(*) FROM access_restrictions WHERE active) AS ProtectedAreas,
                (SELECT count(*) FROM access_ways) AS AccessWays,
                (SELECT count(*) FROM weather_daily) AS WeatherStationDays,
                (SELECT count(*) FROM weather_grid_daily) AS GridDays,
                (SELECT count(*) FROM satellite_scenes) AS Scenes,
                (SELECT max(acquired_at)::date FROM satellite_scenes) AS LatestScene,
                (SELECT count(*) FROM occurrence_records) AS Occurrences,
                (SELECT min(EXTRACT(YEAR FROM measurement_date))::int FROM forest_stands) AS OldestMeasurementYear,
                (SELECT max(EXTRACT(YEAR FROM measurement_date))::int FROM forest_stands) AS NewestMeasurementYear,
                (SELECT max(ingested_at) FROM forest_stands) AS LastStandIngest
            """);
    }

    public async Task<IReadOnlyList<SourceRow>> SourcesAsync()
    {
        await using var db = await openDb();
        return (await db.QueryAsync<SourceRow>("""
            SELECT id, name, provider, license, commercial_use AS CommercialUse, status, coverage, notes, records, last_attempt_at AS LastAttemptAt,
                last_success_at AS LastSuccessAt, endpoint FROM data_sources ORDER BY CASE status WHEN 'active' THEN 0 ELSE 1 END, name
            """)).ToList();
    }

    public async Task<IReadOnlyList<AreaPrediction>> PredictionMixAsync()
    {
        await using var db = await openDb();
        return (await db.QueryAsync<AreaPrediction>("""
            SELECT p.species, mv.kind, p.suitability, count(*) AS Stands
            FROM predictions p JOIN model_versions mv ON mv.id = p.model_version_id AND mv.status = 'production'
            GROUP BY 1, 2, 3 ORDER BY 1, 3
            """)).ToList();
    }

    public async Task<IReadOnlyList<LabelRow2>> LabelsAsync()
    {
        await using var db = await openDb();
        return (await db.QueryAsync<LabelRow2>("""
            SELECT species, qc_status AS Qc, outcome, method, count(*) AS N FROM field_observations WHERE NOT is_synthetic GROUP BY 1, 2, 3, 4 ORDER BY 1, 2
            """)).ToList();
    }

    public async Task<IReadOnlyList<ModelRow>> ModelsAsync()
    {
        await using var db = await openDb();
        return (await db.QueryAsync<ModelRow>("""
            SELECT id, species, kind, version, status, training_rows AS TrainingRows, COALESCE(notes, '') AS Notes, metrics::text AS Metrics, created_at AS CreatedAt,
                promoted_at AS PromotedAt, promoted_by AS PromotedBy FROM model_versions WHERE layer = 'habitat' ORDER BY created_at DESC LIMIT 40
            """)).ToList();
    }

    public async Task<IReadOnlyList<RestrictionRow>> RestrictionsAsync(bool adminOnly)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<RestrictionRow>($"""
            SELECT id, source, name, restriction_type AS RestrictionType, severity, notes, active, created_at AS CreatedAt, created_by AS CreatedBy
            FROM access_restrictions {(adminOnly ? "WHERE source = 'admin'" : "")} ORDER BY created_at DESC LIMIT 100
            """)).ToList();
    }

    public async Task<long> RestrictAreaAsync(long areaId, string reason, string severity, string actor)
    {
        await using var db = await openDb();
        var id = await db.ExecuteScalarAsync<long?>("""
            INSERT INTO access_restrictions (source, source_ref, name, restriction_type, severity, notes, geom, created_by)
            SELECT 'admin', 'area-' || a.id || '-' || extract(epoch FROM now())::bigint, 'Admin: area ' || a.id, 'Marked unavailable by an administrator', @severity, @reason, a.geom, @actor
            FROM picking_areas a WHERE a.id = @areaId RETURNING id
            """, new { areaId, reason, severity, actor });

        if (id is null)
        {
            return 0;
        }

        await Audit.WriteAsync(db, actor, "restriction.add", $"restriction:{id}", new { areaId, severity, reason });
        return id.Value;
    }

    public async Task SetRestrictionActiveAsync(long id, bool active, string actor)
    {
        await using var db = await openDb();
        await db.ExecuteAsync("UPDATE access_restrictions SET active = @active WHERE id = @id AND source = 'admin'", new { id, active });
        await Audit.WriteAsync(db, actor, active ? "restriction.enable" : "restriction.lift", $"restriction:{id}", new { });
    }

    public async Task<Engagement> EngagementAsync()
    {
        await using var db = await openDb();
        return await db.QuerySingleAsync<Engagement>("""
            SELECT count(*) AS Events7d, count(DISTINCT user_id) AS Users7d,
                count(*) FILTER (WHERE event = 'place_search') AS Searches7d, count(*) FILTER (WHERE event = 'area_opened') AS AreasOpened7d,
                count(*) FILTER (WHERE event = 'trip_planned') AS TripsPlanned7d, count(*) FILTER (WHERE event IN ('navigation_handoff', 'trip_navigation')) AS Navigations7d,
                count(*) FILTER (WHERE event = 'observation_submitted') AS Reports7d, count(*) FILTER (WHERE event = 'checkout_started') AS Checkouts7d,
                (SELECT count(*) FROM user_accounts) AS Accounts,
                (SELECT count(DISTINCT user_id) FROM subscriptions WHERE status = 'active' AND offer_id LIKE 'premium%') AS Premium,
                (SELECT count(DISTINCT user_id) FROM subscriptions WHERE status = 'active' AND offer_id LIKE 'professional%') AS Professional
            FROM analytics_events WHERE at > now() - interval '7 days'
            """);
    }

    public async Task<IReadOnlyList<AuditRow>> AuditAsync(int limit = 40)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<AuditRow>("SELECT at, actor, action, target, details::text AS Details FROM audit_logs ORDER BY at DESC LIMIT @limit", new { limit })).ToList();
    }

    public async Task<IReadOnlyList<JobRow>> RecentJobsAsync(int limit = 25)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<JobRow>("""
            SELECT id, source_id AS SourceId, kind, params::text AS Params, status, requested_by AS RequestedBy, created_at AS CreatedAt, started_at AS StartedAt,
                finished_at AS FinishedAt, progress, records_in AS RecordsIn, records_upserted AS RecordsUpserted, message, error
            FROM ingestion_jobs ORDER BY created_at DESC LIMIT @limit
            """, new { limit })).ToList();
    }

    public async Task<IReadOnlyList<(long Id, string Subject)>> ContactAsync()
    {
        await using var db = await openDb();
        return (await db.QueryAsync<(long, string)>("SELECT id, topic || ': ' || left(message, 120) FROM contact_messages WHERE status = 'new' ORDER BY at DESC LIMIT 20")).ToList();
    }

    // Data quality warnings are derived from the stored data, never hard-coded
    public static IReadOnlyList<string> Warnings(CoverageStats c, IReadOnlyList<SourceRow> sources, IReadOnlyList<ModelRow> models, IReadOnlyList<LabelRow2> labels)
    {
        var warnings = new List<string>();
        if (c.Stands == 0) warnings.Add("No forest stands are stored yet: run the data pipeline.");
        if (c.Tiles > c.TilesWithStands) warnings.Add($"{c.Tiles - c.TilesWithStands} of {c.Tiles} pilot tiles have no stand data yet.");
        if (c.Stands > 0 && c.SoilShare < 0.9) warnings.Add($"Only {c.SoilShare:P0} of stands have a joined GTK soil class.");
        if (c.Stands > 0 && c.AccessShare < 0.8) warnings.Add($"Only {c.AccessShare:P0} of stands have a mapped road within 10 km.");
        if (c.OldestMeasurementYear is < 2015) warnings.Add($"Some stands were last measured in {c.OldestMeasurementYear}; their evidence is rated lower.");
        if (c.WeatherStationDays == 0) warnings.Add("No FMI weather observations are stored: seasonal estimates fall back to the NASA model grid or stay unknown.");

        foreach (var s in sources.Where(s => s.Status == "active" && s.LastSuccessAt is { } t && DateTime.UtcNow - t > TimeSpan.FromDays(45)))
        {
            warnings.Add($"{s.Name} was last refreshed {(DateTime.UtcNow - s.LastSuccessAt!.Value).Days} days ago.");
        }

        foreach (var s in sources.Where(s => s.Status is "pending_authorization" or "pending_credentials"))
        {
            warnings.Add($"{s.Name}: {s.Status.Replace('_', ' ')}.");
        }

        var accepted = labels.Where(l => l.Qc == "accepted").Sum(l => l.N);
        warnings.Add(accepted == 0
            ? "No accepted field observations yet: there is nothing to train on, so every prediction is an unvalidated ecological estimate."
            : $"{accepted} accepted field observations; training needs at least {TrainingPipeline.MinRows} usable labels across {TrainingPipeline.MinBlocks} spatial blocks.");

        if (!models.Any(m => m.Kind != "heuristic" && m.Status == "production")) warnings.Add("No trained model is in production. The map shows the ecological baseline, labelled as unvalidated.");
        return warnings;
    }
}
