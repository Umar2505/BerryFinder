using System.Collections.Concurrent;
using System.Globalization;
using Dapper;

public record FavoriteRequest(string Label, string? Species, long? AreaId, double Lat, double Lon, string? Notes);

public record TripRequest(double Lat, double Lon, string Species, int AvailableMinutes, double MaxDriveKm, double? MaxWalkM);

public record JobRequest(string Kind, bool OnlyMissing = false);

// The public and authenticated HTTP API, served under /api. Public reads are rate limited per client;
// user routes need an app-issued bearer token (EndpointAuth.User); admin routes also check the role.
public partial class BerryfinderApp
{
    private static readonly ConcurrentDictionary<string, (DateTime Window, int Count)> RateWindows = new();
    private static readonly ConcurrentDictionary<string, byte[]> TileCache = new();

    private static bool RateLimited(Ikon.App.HttpRequest request, int perMinute)
    {
        var key = request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) ? forwarded.Split(',')[0].Trim() : "unknown";
        var now = DateTime.UtcNow;
        var entry = RateWindows.AddOrUpdate(key, _ => (now, 1), (_, e) => now - e.Window > TimeSpan.FromMinutes(1) ? (now, 1) : (e.Window, e.Count + 1));

        if (RateWindows.Count > 50_000)
        {
            RateWindows.Clear();
        }

        return entry.Count > perMinute;
    }

    private static double? Q(Ikon.App.HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static string? QS(Ikon.App.HttpRequest request, string name) => request.Query.TryGetValue(name, out var raw) && raw.Length > 0 ? raw : null;

    [HttpGet("/health", Auth = EndpointAuth.Public)]
    public async Task<HttpResult> Health()
    {
        try
        {
            await using var db = await OpenDbAsync();
            var stands = await db.ExecuteScalarAsync<long>("SELECT count(*) FROM forest_stands");
            var lastJob = await db.ExecuteScalarAsync<DateTime?>("SELECT max(finished_at) FROM ingestion_jobs WHERE status = 'succeeded'");
            return HttpResult.Ok(new { status = "ok", database = "ok", stands, lastSuccessfulJob = lastJob, models = _publisher.Production });
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Health check failed: {ex.Message}");
            return HttpResult.Json(new { status = "degraded", database = "unreachable" }, 503);
        }
    }

    [HttpGet("/species", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public object GetSpecies() => Registry.SpeciesRows.Select(s => new { s.Id, s.Scientific, s.NameEn, s.NameFi, s.Supported, s.Notes });

    // Mapbox vector tiles straight from PostGIS: merged picking areas at every zoom, individual stands from zoom 12
    [HttpGet("/tiles/{species}/{z}/{x}/{y}", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public async Task<HttpResult> GetTile(string species, int z, int x, int y)
    {
        if (!Species.Supported.Contains(species) || z is < 7 or > 16 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z)
        {
            return HttpResult.NotFound("No such tile");
        }

        if (!_publisher.Production.TryGetValue(species, out var mv))
        {
            return HttpResult.Bytes([], "application/vnd.mapbox-vector-tile");
        }

        var key = $"{species}/{mv}/{_dataVersion.Value}/{z}/{x}/{y}";
        if (!TileCache.TryGetValue(key, out var bytes))
        {
            await using var db = await OpenDbAsync();
            bytes = await db.ExecuteScalarAsync<byte[]>(new CommandDefinition("""
                WITH bounds AS (SELECT ST_TileEnvelope(@z, @x, @y) AS geom),
                areas AS (
                    SELECT a.id, a.suitability, round(a.area_ha::numeric, 1)::float8 AS area_ha, (a.restriction IS NOT NULL) AS restricted,
                        ST_AsMVTGeom(a.geom_web, bounds.geom, 4096, 64, true) AS geom
                    FROM picking_areas a, bounds
                    WHERE a.model_version_id = @mv AND a.species = @species AND a.geom_web && bounds.geom
                ),
                stands AS (
                    SELECT s.stand_id AS id, p.suitability, p.evidence, p.excluded,
                        ST_AsMVTGeom(s.geom_web, bounds.geom, 4096, 64, true) AS geom
                    FROM forest_stands s
                    JOIN predictions p ON p.stand_id = s.stand_id AND p.model_version_id = @mv AND p.species = @species, bounds
                    WHERE @z >= 12 AND s.geom_web && bounds.geom
                )
                SELECT COALESCE((SELECT ST_AsMVT(areas.*, 'areas', 4096, 'geom') FROM areas WHERE geom IS NOT NULL), ''::bytea)
                    || COALESCE((SELECT ST_AsMVT(stands.*, 'stands', 4096, 'geom') FROM stands WHERE geom IS NOT NULL), ''::bytea)
                """, new { z, x, y, mv, species }, commandTimeout: 60)) ?? [];

            if (TileCache.Count > 20_000)
            {
                TileCache.Clear();
            }

            TileCache[key] = bytes;
        }

        return HttpResult.Bytes(bytes, "application/vnd.mapbox-vector-tile").WithHeader("Cache-Control", "public, max-age=600");
    }

    [HttpGet("/areas/search", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public async Task<HttpResult> SearchAreas(Ikon.App.HttpRequest request)
    {
        if (RateLimited(request, 60)) return HttpResult.Json(new { error = "Too many requests" }, 429).WithHeader("Retry-After", "60");

        var species = QS(request, "species") ?? Species.Bilberry;
        if (!Species.Supported.Contains(species) || Q(request, "lat") is not { } lat || Q(request, "lon") is not { } lon || lat is < 59 or > 71 || lon is < 19 or > 32)
        {
            return HttpResult.BadRequest("species, lat and lon (inside Finland) are required");
        }

        // The anonymous API returns the free tier's view: the top candidates only
        var results = await _queries.SearchAreasAsync(species, lat, lon, Q(request, "radiusKm") ?? 25, false, Q(request, "maxWalkM"), true, Plans.Free.RecommendationLimit);
        return HttpResult.Ok(new { species, predictionKind = results.FirstOrDefault()?.PredictionKind ?? "unavailable", results, note = Disclaimer() });
    }

    [HttpGet("/areas/{id}", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public async Task<HttpResult> GetArea(long id, Ikon.App.HttpRequest request)
    {
        if (RateLimited(request, 120)) return HttpResult.Json(new { error = "Too many requests" }, 429);
        var detail = await _queries.AreaDetailAsync(id, Q(request, "lat") ?? 65.01, Q(request, "lon") ?? 25.47);
        return detail is null ? HttpResult.NotFound("No such area") : HttpResult.Ok(new { detail, note = Disclaimer() });
    }

    [HttpGet("/predictions", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public async Task<HttpResult> GetPredictions(Ikon.App.HttpRequest request)
    {
        if (RateLimited(request, 60)) return HttpResult.Json(new { error = "Too many requests" }, 429);

        var species = QS(request, "species") ?? Species.Bilberry;
        var bbox = (QS(request, "bbox") ?? "").Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();

        if (!Species.Supported.Contains(species) || bbox.Length != 4 || bbox.Any(double.IsNaN) || bbox[2] <= bbox[0] || bbox[3] <= bbox[1] || bbox[2] - bbox[0] > 1 || bbox[3] - bbox[1] > 0.5)
        {
            return HttpResult.BadRequest("species and bbox=west,south,east,north (at most 1° by 0.5°) are required");
        }

        var areas = await _queries.AreasInViewAsync(species, bbox[0], bbox[1], bbox[2], bbox[3], (bbox[1] + bbox[3]) / 2, (bbox[0] + bbox[2]) / 2, false, null, false, 100);
        return HttpResult.Ok(new
        {
            type = "FeatureCollection",
            features = areas.Select(a => new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { a.Lon, a.Lat } },
                properties = new { a.Id, a.Suitability, a.AreaHa, a.PredictionKind, a.Restriction },
            }),
        });
    }

    [HttpGet("/forecast", Auth = EndpointAuth.Public, AllowAnyOrigin = true)]
    public async Task<HttpResult> GetForecast(Ikon.App.HttpRequest request)
    {
        var species = QS(request, "species") ?? Species.Bilberry;
        if (!Species.Supported.Contains(species)) return HttpResult.BadRequest("Unknown species");

        var lat = Q(request, "lat") ?? 65.01;
        var lon = Q(request, "lon") ?? 25.47;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weather = await _queries.WeatherNearAsync(lat, lon, today.Year);
        var estimate = Phenology.Estimate(species, today, weather?.Days ?? [], weather?.NormalJunJulPrecip);
        return HttpResult.Ok(new
        {
            species, date = today, kind = "heuristic", model = Phenology.Version, estimate.Stage, estimate.Availability, estimate.Gdd, estimate.Notes, estimate.Basis,
            station = weather is null ? null : new { weather.Fmisid, weather.Name, distanceKm = Math.Round(weather.DistanceKm, 1), lastObservation = estimate.AsOf },
            note = "Seasonal window from station weather; not a yield forecast",
        });
    }

    [HttpPost("/trips/plan", Auth = EndpointAuth.User)]
    public async Task<HttpResult> PlanTrip(TripRequest request)
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        if (!Species.Supported.Contains(request.Species)) return HttpResult.BadRequest("Unknown species");
        var plan = await PlanTripCoreAsync(userId, request);
        return HttpResult.Ok(plan);
    }

    [HttpGet("/users/me", Auth = EndpointAuth.User)]
    public async Task<HttpResult> GetMe()
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        await using var db = await OpenDbAsync();
        var account = await db.QuerySingleOrDefaultAsync("SELECT user_id, role, language, consent_share_aggregated, consent_training, created_at FROM user_accounts WHERE user_id = @userId", new { userId });
        return account is null ? HttpResult.NotFound("No account yet; open the app once") : HttpResult.Ok(account);
    }

    [HttpGet("/users/favorites", Auth = EndpointAuth.User)]
    public async Task<HttpResult> GetFavorites()
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        return HttpResult.Ok(await _queries.FavoritesAsync(userId));
    }

    [HttpPost("/users/favorites", Auth = EndpointAuth.User)]
    public async Task<HttpResult> AddFavorite(FavoriteRequest request)
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Label) || request.Lat is < 59 or > 71 || request.Lon is < 19 or > 32) return HttpResult.BadRequest("A label and a location inside Finland are required");
        var count = (await _queries.FavoritesAsync(userId)).Count;
        if (count >= Plans.Free.SavedLimit && !await HasPremiumAsync(userId)) return HttpResult.Forbidden($"The free plan keeps up to {Plans.Free.SavedLimit} saved places");
        var id = await _queries.AddFavoriteAsync(userId, request.Label.Trim(), request.Species, request.AreaId, request.Lat, request.Lon, request.Notes);
        return HttpResult.Created(new { id });
    }

    [HttpPost("/observations", Auth = EndpointAuth.User)]
    public async Task<HttpResult> PostObservation(ObservationInput input)
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        var result = await _observations.SubmitAsync(userId, input with { Photos = [] });
        return result.Id is { } id ? HttpResult.Created(new { id, flags = result.Flags }) : HttpResult.BadRequest(result.Error);
    }

    [HttpGet("/observations/me", Auth = EndpointAuth.User)]
    public async Task<HttpResult> GetMyObservations()
    {
        if (ProvenUser.Current is not { } userId) return HttpResult.Unauthorized();
        return HttpResult.Ok(await _queries.MyObservationsAsync(userId));
    }

    private async Task<bool> ProvenAdminAsync() => ProvenUser.Current is { } userId && await IsAdminAsync(userId, null);

    [HttpGet("/admin/data-sources", Auth = EndpointAuth.User)]
    public async Task<HttpResult> AdminDataSources()
    {
        if (!await ProvenAdminAsync()) return HttpResult.Forbidden("Admins only");
        await using var db = await OpenDbAsync();
        return HttpResult.Ok(await db.QueryAsync("SELECT * FROM data_sources ORDER BY id"));
    }

    [HttpPost("/admin/ingestion/run", Auth = EndpointAuth.User)]
    public async Task<HttpResult> AdminRunIngestion(JobRequest request)
    {
        if (!await ProvenAdminAsync()) return HttpResult.Forbidden("Admins only");
        if (!JobKinds.All.Contains(request.Kind)) return HttpResult.BadRequest($"kind must be one of {string.Join(", ", JobKinds.All)}");
        var id = await _jobs.EnqueueAsync(request.Kind, new { onlyMissing = request.OnlyMissing }, ProvenUser.Current);
        return HttpResult.Accepted(new { jobId = id });
    }

    [HttpPost("/admin/models/train", Auth = EndpointAuth.User)]
    public async Task<HttpResult> AdminTrain()
    {
        if (!await ProvenAdminAsync()) return HttpResult.Forbidden("Admins only");
        var id = await _jobs.EnqueueAsync(JobKinds.Train, null, ProvenUser.Current);
        return HttpResult.Accepted(new { jobId = id });
    }

    [HttpGet("/admin/models/metrics", Auth = EndpointAuth.User)]
    public async Task<HttpResult> AdminModelMetrics()
    {
        if (!await ProvenAdminAsync()) return HttpResult.Forbidden("Admins only");
        await using var db = await OpenDbAsync();
        return HttpResult.Ok(await db.QueryAsync("SELECT id, species, layer, kind, version, status, metrics, training_rows, created_at, promoted_at FROM model_versions ORDER BY created_at DESC LIMIT 100"));
    }

    private string Disclaimer() =>
        "Estimates are uncertain and cannot guarantee any harvest. Check access rules on site; everyman's rights do not apply in every protected area or private yard.";
}
