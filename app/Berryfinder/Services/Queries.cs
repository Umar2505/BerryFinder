using System.Text.Json;
using Dapper;

public sealed record AreaSummary(long Id, string Species, string Suitability, double MeanScore, double AreaHa, int StandCount, double HighShare,
    double? DistRoadM, double? AccessLat, double? AccessLon, string? Restriction, double Lat, double Lon, double DistanceKm, string PredictionKind);

public sealed record Share(string Key, double Fraction);

public sealed record DriverSummary(string Factor, string Effect, string En, string Fi, double Weight);

public sealed record AreaDetail(
    AreaSummary Area,
    string ModelVersion, string ModelKind, DateTime PublishedAt,
    double? MeanAge, double? BasalArea, double? MeanHeight,
    IReadOnlyList<Share> Fertility, IReadOnlyList<Share> Development, IReadOnlyList<Share> Soil, IReadOnlyList<Share> SiteMain,
    double? PropPine, double? PropSpruce, double? PropOther,
    DateOnly? OldestMeasurement, DateOnly? NewestMeasurement, DateTime? IngestedAt,
    IReadOnlyList<DriverSummary> Drivers,
    IReadOnlyList<Share> Evidence,
    string? RestrictionSeverity,
    CommunitySummary? Community);

public sealed record CommunitySummary(int Reports, int Contributors, int Found, int NotFound, DateOnly? Latest);

public sealed record StationWeather(int Fmisid, string Name, double DistanceKm, IReadOnlyList<Phenology.DailyWeather> Days, double? NormalJunJulPrecip);

public sealed record SavedLocation(long Id, string Label, string? Species, long? AreaId, double Lat, double Lon, string? Notes, DateTime CreatedAt);

public sealed record ObservationRow(long Id, string Species, DateOnly ObservedOn, string Method, string Outcome, string? Stage, int? EffortMinutes,
    double? AmountLitres, string QcStatus, string? QcNote, bool ShareAggregated, bool UseForTraining, double Lat, double Lon, DateTime SubmittedAt, int Photos);

public sealed class Queries(OpenDb openDb, PredictionPublisher publisher)
{
    private sealed record StandAgg(long StandId, double Area, float? MeanAge, float? BasalArea, float? MeanHeight, short? Fertility, string? Development,
        string? SoilClass, string? SiteMain, float? PropPine, float? PropSpruce, float? PropOther, DateOnly? Measured, DateTime IngestedAt, string Drivers,
        string Evidence, string? Severity);

    private long? ProductionId(string species) => publisher.Production.TryGetValue(species, out var id) ? id : null;

    private const string AreaColumns = """
        a.id, a.species, a.suitability, a.mean_score AS MeanScore, a.area_ha AS AreaHa, a.stand_count AS StandCount, a.high_share AS HighShare,
        a.dist_road_m AS DistRoadM, ST_Y(a.access_point) AS AccessLat, ST_X(a.access_point) AS AccessLon, a.restriction,
        ST_Y(a.centroid) AS Lat, ST_X(a.centroid) AS Lon
        """;

    private sealed record AreaRaw(long Id, string Species, string Suitability, float MeanScore, float AreaHa, int StandCount, float HighShare, float? DistRoadM,
        double? AccessLat, double? AccessLon, string? Restriction, double Lat, double Lon, double DistanceKm, string Kind);

    private static AreaSummary ToSummary(AreaRaw r) => new(r.Id, r.Species, r.Suitability, r.MeanScore, r.AreaHa, r.StandCount, r.HighShare, r.DistRoadM,
        r.AccessLat, r.AccessLon, r.Restriction, r.Lat, r.Lon, r.DistanceKm, r.Kind);

    // Ranked candidate areas near a point. Suitability first, then score, then closeness.
    public async Task<IReadOnlyList<AreaSummary>> SearchAreasAsync(string species, double lat, double lon, double radiusKm, bool highOnly,
        double? maxWalkM, bool hideRestricted, int limit, int offset = 0)
    {
        if (ProductionId(species) is not { } mv)
        {
            return [];
        }

        radiusKm = Math.Clamp(radiusKm, 1, 100);
        limit = Math.Clamp(limit, 1, 200);
        await using var db = await openDb();
        var rows = await db.QueryAsync<AreaRaw>($"""
            SELECT {AreaColumns}, ST_Distance(a.centroid::geography, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography) / 1000 AS DistanceKm, mv.kind
            FROM picking_areas a JOIN model_versions mv ON mv.id = a.model_version_id
            WHERE a.model_version_id = @mv AND a.species = @species
              AND ST_DWithin(a.centroid::geography, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography, @radius)
              AND (NOT @highOnly OR a.suitability = 'high')
              AND (CAST(@maxWalk AS real) IS NULL OR a.dist_road_m <= CAST(@maxWalk AS real))
              AND (NOT @hideRestricted OR a.restriction IS NULL)
            ORDER BY (a.suitability = 'high') DESC, a.mean_score * least(1, sqrt(a.area_ha / 5)) DESC, DistanceKm
            LIMIT @limit OFFSET @offset
            """, new { mv, species, lat, lon, radius = radiusKm * 1000, highOnly, maxWalk = maxWalkM, hideRestricted, limit, offset });
        return rows.Select(ToSummary).ToList();
    }

    // Areas inside the visible map box, for the list that follows the map
    public async Task<IReadOnlyList<AreaSummary>> AreasInViewAsync(string species, double west, double south, double east, double north, double refLat, double refLon,
        bool highOnly, double? maxWalkM, bool hideRestricted, int limit = 40)
    {
        if (ProductionId(species) is not { } mv || east - west > 3 || north - south > 2)
        {
            return [];
        }

        await using var db = await openDb();
        var rows = await db.QueryAsync<AreaRaw>($"""
            SELECT {AreaColumns}, ST_Distance(a.centroid::geography, ST_SetSRID(ST_MakePoint(@refLon, @refLat), 4326)::geography) / 1000 AS DistanceKm, mv.kind
            FROM picking_areas a JOIN model_versions mv ON mv.id = a.model_version_id
            WHERE a.model_version_id = @mv AND a.species = @species AND a.centroid && ST_MakeEnvelope(@west, @south, @east, @north, 4326)
              AND (NOT @highOnly OR a.suitability = 'high')
              AND (CAST(@maxWalk AS real) IS NULL OR a.dist_road_m <= CAST(@maxWalk AS real))
              AND (NOT @hideRestricted OR a.restriction IS NULL)
            ORDER BY (a.suitability = 'high') DESC, a.mean_score * least(1, sqrt(a.area_ha / 5)) DESC
            LIMIT @limit
            """, new { mv, species, west, south, east, north, refLat, refLon, highOnly, maxWalk = maxWalkM, hideRestricted, limit = Math.Clamp(limit, 1, 100) });
        return rows.Select(ToSummary).ToList();
    }

    public async Task<AreaDetail?> AreaDetailAsync(long id, double refLat, double refLon)
    {
        await using var db = await openDb();
        var raw = await db.QuerySingleOrDefaultAsync<AreaRaw>($"""
            SELECT {AreaColumns}, ST_Distance(a.centroid::geography, ST_SetSRID(ST_MakePoint(@refLon, @refLat), 4326)::geography) / 1000 AS DistanceKm, mv.kind
            FROM picking_areas a JOIN model_versions mv ON mv.id = a.model_version_id WHERE a.id = @id
            """, new { id, refLat, refLon });

        if (raw is null)
        {
            return null;
        }

        var meta = await db.QuerySingleAsync<(string Version, string Kind, DateTime PublishedAt, long ModelId)>("""
            SELECT mv.version, mv.kind, a.published_at, mv.id FROM picking_areas a JOIN model_versions mv ON mv.id = a.model_version_id WHERE a.id = @id
            """, new { id });

        var stands = (await db.QueryAsync<StandAgg>("""
            SELECT s.stand_id AS StandId, ST_Area(s.geom) AS Area, s.mean_age AS MeanAge, s.basal_area AS BasalArea, s.mean_height AS MeanHeight,
                s.fertility_class AS Fertility, s.development_class AS Development, sf.soil_class AS SoilClass, s.site_main_class AS SiteMain,
                s.prop_pine AS PropPine, s.prop_spruce AS PropSpruce, s.prop_other AS PropOther, s.measurement_date AS Measured, s.ingested_at AS IngestedAt,
                p.drivers::text AS Drivers, p.evidence AS Evidence, af.restriction_severity AS Severity
            FROM picking_areas a
            JOIN forest_stands s ON s.geom && a.geom AND ST_Contains(a.geom, ST_PointOnSurface(s.geom))
            JOIN predictions p ON p.stand_id = s.stand_id AND p.model_version_id = a.model_version_id AND p.species = a.species
            LEFT JOIN soil_features sf ON sf.stand_id = s.stand_id
            LEFT JOIN access_features af ON af.stand_id = s.stand_id
            WHERE a.id = @id
            """, new { id })).ToList();

        var totalArea = Math.Max(1, stands.Sum(s => s.Area));

        double? Weighted(Func<StandAgg, float?> pick)
        {
            var known = stands.Where(s => pick(s) is not null).ToList();
            return known.Count == 0 ? null : known.Sum(s => s.Area * pick(s)!.Value) / known.Sum(s => s.Area);
        }

        IReadOnlyList<Share> Shares(Func<StandAgg, string?> pick) =>
            stands.GroupBy(s => pick(s) ?? "unknown").Select(g => new Share(g.Key, g.Sum(s => s.Area) / totalArea)).OrderByDescending(s => s.Fraction).ToList();

        // Reasons shared by most of the area, weighted by stand area
        var drivers = stands
            .SelectMany(s => JsonSerializer.Deserialize<List<JsonElement>>(s.Drivers)!.Select(d => (s.Area, d)))
            .GroupBy(x => (Factor: x.d.GetProperty("f").GetString()!, Effect: x.d.GetProperty("effect").GetString()!, En: x.d.GetProperty("en").GetString()!, Fi: x.d.GetProperty("fi").GetString()!))
            .Select(g => new DriverSummary(g.Key.Factor, g.Key.Effect, g.Key.En, g.Key.Fi, g.Sum(x => x.Area) / totalArea))
            .OrderByDescending(d => d.Weight)
            .GroupBy(d => d.Factor).Select(g => g.First())
            .OrderByDescending(d => d.Effect == "positive").ThenByDescending(d => d.Weight)
            .Take(6)
            .ToList();

        var community = await db.QuerySingleAsync<(int Reports, int Contributors, int Found, int NotFound, DateOnly? Latest)>("""
            SELECT count(*)::int, count(DISTINCT o.user_id)::int,
                count(*) FILTER (WHERE o.outcome IN ('few', 'moderate', 'many', 'unripe'))::int,
                count(*) FILTER (WHERE o.outcome = 'none')::int, max(o.observed_on)
            FROM field_observations o JOIN picking_areas a ON a.id = @id
            WHERE o.species = a.species AND o.share_aggregated AND o.qc_status = 'accepted' AND NOT o.is_synthetic
              AND ST_Intersects(ST_Transform(o.geom, 3067), a.geom)
            """, new { id });

        return new AreaDetail(ToSummary(raw), meta.Version, meta.Kind, meta.PublishedAt,
            Weighted(s => s.MeanAge), Weighted(s => s.BasalArea), Weighted(s => s.MeanHeight),
            Shares(s => s.Fertility?.ToString()), Shares(s => s.Development), Shares(s => s.SoilClass), Shares(s => s.SiteMain),
            Weighted(s => s.PropPine), Weighted(s => s.PropSpruce), Weighted(s => s.PropOther),
            stands.Where(s => s.Measured is not null).Select(s => s.Measured).Min(), stands.Where(s => s.Measured is not null).Select(s => s.Measured).Max(),
            stands.Count == 0 ? null : stands.Max(s => s.IngestedAt),
            drivers, Shares(s => s.Evidence),
            stands.Any(s => s.Severity == "exclude") ? "exclude" : stands.Any(s => s.Severity == "warn") ? "warn" : null,
            // Aggregated community reports are shown only when at least three people contributed, so no one's spot is exposed
            community.Contributors >= 3 ? new CommunitySummary(community.Reports, community.Contributors, community.Found, community.NotFound, community.Latest) : null);
    }

    public async Task<long?> AreaAtAsync(string species, double lat, double lon)
    {
        if (ProductionId(species) is not { } mv)
        {
            return null;
        }

        await using var db = await openDb();
        return await db.ExecuteScalarAsync<long?>("""
            SELECT id FROM picking_areas WHERE model_version_id = @mv AND species = @species
              AND ST_Intersects(geom, ST_Transform(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), 3067)) LIMIT 1
            """, new { mv, species, lat, lon });
    }

    public async Task<StationWeather?> WeatherNearAsync(double lat, double lon, int year)
    {
        await using var db = await openDb();
        var station = await db.QuerySingleOrDefaultAsync<(int Fmisid, string Name, double DistanceKm)>("""
            SELECT ws.fmisid, ws.name, ST_Distance(ws.geom::geography, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography) / 1000
            FROM weather_stations ws WHERE EXISTS (SELECT 1 FROM weather_daily d WHERE d.fmisid = ws.fmisid AND d.day >= make_date(@year, 4, 1))
            ORDER BY ws.geom <-> ST_SetSRID(ST_MakePoint(@lon, @lat), 4326) LIMIT 1
            """, new { lat, lon, year });

        if (station.Name is null)
        {
            return null;
        }

        var days = (await db.QueryAsync<(DateOnly Day, float? TMean, float? TMin, float? Precip, float? Snow)>("""
            SELECT day, t_mean, t_min, precip_mm, snow_cm FROM weather_daily WHERE fmisid = @fmisid AND day BETWEEN make_date(@year, 3, 25) AND make_date(@year, 12, 31) ORDER BY day
            """, new { station.Fmisid, year }))
            .Select(d => new Phenology.DailyWeather(d.Day, d.TMean, d.TMin, d.Precip, d.Snow)).ToList();

        // Normal = mean June–July rainfall over the earlier seasons stored for this station (at least three)
        var normals = (await db.QueryAsync<(int Year, double Total, int Days)>("""
            SELECT EXTRACT(YEAR FROM day)::int, sum(precip_mm), count(precip_mm)::int FROM weather_daily
            WHERE fmisid = @fmisid AND EXTRACT(MONTH FROM day) IN (6, 7) AND EXTRACT(YEAR FROM day) < @year GROUP BY 1
            """, new { station.Fmisid, year })).Where(n => n.Days >= 55).ToList();

        return new StationWeather(station.Fmisid, station.Name, station.DistanceKm, days, normals.Count >= 3 ? normals.Average(n => n.Total) : null);
    }

    // NASA POWER model-grid weather for the cell containing the point (modelled, not station data)
    public async Task<IReadOnlyList<Phenology.DailyWeather>> GridWeatherAsync(double lat, double lon, int year)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<(DateOnly Day, float? TMean, float? TMin, float? Precip, float? Wet)>("""
            SELECT day, t_mean, t_min, precip_mm, root_wetness FROM weather_grid_daily
            WHERE grid_id = @gridId AND day BETWEEN make_date(@year, 3, 25) AND make_date(@year, 12, 31) ORDER BY day
            """, new { gridId = NasaPowerParser.GridId(lat, lon), year }))
            .Select(d => new Phenology.DailyWeather(d.Day, d.TMean, d.TMin, d.Precip, null, d.Wet)).ToList();
    }

    public async Task<IReadOnlyList<SavedLocation>> FavoritesAsync(string userId)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<SavedLocation>("""
            SELECT id, label, species, area_id AS AreaId, ST_Y(geom) AS Lat, ST_X(geom) AS Lon, notes, created_at AS CreatedAt
            FROM saved_locations WHERE user_id = @userId ORDER BY created_at DESC
            """, new { userId })).ToList();
    }

    public async Task<long> AddFavoriteAsync(string userId, string label, string? species, long? areaId, double lat, double lon, string? notes)
    {
        await using var db = await openDb();
        await EnsureUserAsync(db, userId);
        return await db.ExecuteScalarAsync<long>("""
            INSERT INTO saved_locations (user_id, label, species, area_id, geom, notes)
            VALUES (@userId, @label, @species, @areaId, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), @notes) RETURNING id
            """, new { userId, label = label.Length > 120 ? label[..120] : label, species, areaId, lat, lon, notes });
    }

    public async Task<SavedLocation?> RemoveFavoriteAsync(string userId, long id)
    {
        await using var db = await openDb();
        return await db.QuerySingleOrDefaultAsync<SavedLocation>("""
            DELETE FROM saved_locations WHERE id = @id AND user_id = @userId
            RETURNING id, label, species, area_id AS AreaId, ST_Y(geom) AS Lat, ST_X(geom) AS Lon, notes, created_at AS CreatedAt
            """, new { id, userId });
    }

    public async Task<IReadOnlyList<ObservationRow>> MyObservationsAsync(string userId, int limit = 100)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<ObservationRow>("""
            SELECT o.id, o.species, o.observed_on AS ObservedOn, o.method, o.outcome, o.stage, o.effort_minutes AS EffortMinutes, o.amount_litres::float8 AS AmountLitres,
                o.qc_status AS QcStatus, o.qc_note AS QcNote, o.share_aggregated AS ShareAggregated, o.use_for_training AS UseForTraining,
                ST_Y(o.geom) AS Lat, ST_X(o.geom) AS Lon, o.submitted_at AS SubmittedAt,
                (SELECT count(*)::int FROM observation_media m WHERE m.observation_id = o.id) AS Photos
            FROM field_observations o WHERE o.user_id = @userId ORDER BY o.submitted_at DESC LIMIT @limit
            """, new { userId, limit })).ToList();
    }

    public static async Task EnsureUserAsync(System.Data.Common.DbConnection db, string userId) =>
        await db.ExecuteAsync("""
            INSERT INTO user_accounts (user_id) VALUES (@userId) ON CONFLICT (user_id) DO UPDATE SET last_seen_at = now()
            """, new { userId });

    public async Task TrackAsync(string? userId, string name, object? props = null)
    {
        try
        {
            await using var db = await openDb();
            await db.ExecuteAsync("INSERT INTO analytics_events (user_id, event, props) VALUES (@userId, @name, CAST(@props AS jsonb))",
                new { userId, name, props = JsonSerializer.Serialize(props ?? new { }) });
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Analytics write failed: {ex.Message}");
        }
    }
}
