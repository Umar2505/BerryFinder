using System.Text.Json;
using Dapper;

public sealed record ObservationInput(
    string Species, double Lat, double Lon, double? GpsAccuracyM, string LocationSource, DateOnly ObservedOn,
    string Method, string Outcome, string? Stage, int? EffortMinutes, double? AreaSearchedM2, double? AmountLitres,
    int? PlotCount, int? RipeCount, int? UnripeCount, int? FlowerCount, long? AreaId,
    bool ShareAggregated, bool UseForTraining, string? Notes, IReadOnlyList<PhotoRef> Photos);

public sealed record PhotoRef(string AssetUri, string MimeType, int Width, int Height, long Bytes, IReadOnlyList<string> QualityFlags);

public sealed record SubmitResult(long? Id, string? Error, IReadOnlyList<string> Flags);

public sealed record ReviewItem(long Id, string UserId, string Species, DateOnly ObservedOn, DateTime SubmittedAt, string Method, string Outcome, string? Stage,
    int? EffortMinutes, double? AreaSearchedM2, double? AmountLitres, int? PlotCount, int? RipeCount, int? UnripeCount, double? GpsAccuracyM, string LocationSource,
    string QcStatus, string QcFlags, string? QcNote, bool UseForTraining, bool ShareAggregated, long? DuplicateOf, string? Notes, double Lat, double Lon,
    string? StandSuitability, int UserSubmissions, int Photos);

// Field observations: validation, spam and duplicate checks, and the moderation workflow.
// Coordinates are private: they are only ever returned to their owner and to admins.
public sealed class ObservationService(OpenDb openDb)
{
    public static readonly string[] Methods = ["visit", "plot_1m2"];
    public static readonly string[] Outcomes = ["none", "few", "moderate", "many", "unripe", "not_surveyed"];
    public static readonly string[] Stages = ["flowering", "unripe", "ripe", "overripe"];
    public const int DailyLimit = 30;

    public static string? Validate(ObservationInput o, DateOnly today)
    {
        if (!BerryCatalog.TryGet(o.Species, out _)) return "Choose which berry you looked for";
        if (!Methods.Contains(o.Method)) return "Unknown survey method";
        if (!Outcomes.Contains(o.Outcome)) return "Choose what you found";
        if (o.Stage is not null && !Stages.Contains(o.Stage)) return "Unknown ripeness stage";
        if (o.Lat is < 59.5 or > 70.2 || o.Lon is < 19.0 or > 31.7) return "The location is outside Finland";
        if (o.ObservedOn > today) return "The survey date is in the future";
        if (o.ObservedOn < today.AddDays(-400)) return "Observations older than a year can't be added here";
        if (o.Method == "plot_1m2" && o.Outcome != "not_surveyed" && (o.PlotCount is null or < 1)) return "Say how many 1 m² plots you counted";
        if (o.EffortMinutes is < 0 or > 1440) return "Time spent must be between 0 and 24 hours";
        if (o.AmountLitres is < 0) return "Amount collected can't be negative";
        return null;
    }

    public async Task<SubmitResult> SubmitAsync(string userId, ObservationInput o)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (Validate(o, today) is { } error)
        {
            return new SubmitResult(null, error, []);
        }

        await using var db = await openDb();
        await Queries.EnsureUserAsync(db, userId);

        var recent = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM field_observations WHERE user_id = @userId AND submitted_at > now() - interval '1 day'", new { userId });
        if (recent >= DailyLimit)
        {
            return new SubmitResult(null, $"You've sent {DailyLimit} observations today, the daily limit. Try again tomorrow.", []);
        }

        var flags = new List<string>();

        // Same person, species and day within 30 m: almost certainly a double submission
        var duplicate = await db.ExecuteScalarAsync<long?>("""
            SELECT id FROM field_observations WHERE user_id = @userId AND species = @Species AND observed_on = @ObservedOn
              AND ST_DWithin(geom::geography, ST_SetSRID(ST_MakePoint(@Lon, @Lat), 4326)::geography, 30) ORDER BY submitted_at DESC LIMIT 1
            """, new { userId, o.Species, o.ObservedOn, o.Lat, o.Lon });
        if (duplicate is not null) flags.Add("possible_duplicate");

        if (o.GpsAccuracyM is > 100) flags.Add("low_gps_accuracy");
        if (o.LocationSource != "gps") flags.Add("location_not_from_gps");
        if (o.RipeCount + o.UnripeCount is { } berries && o.PlotCount is > 0 && berries / (double)o.PlotCount > 600) flags.Add("implausible_plot_count");
        if (o.AmountLitres is > 60) flags.Add("implausible_amount");
        if (o.Outcome is "many" && o.ObservedOn.Month is < 6) flags.Add("out_of_season");
        if (recent >= 10) flags.Add("high_submission_rate");

        var stand = await db.QuerySingleOrDefaultAsync<long?>("""
            SELECT stand_id FROM forest_stands WHERE ST_Intersects(geom, ST_Transform(ST_SetSRID(ST_MakePoint(@Lon, @Lat), 4326), 3067)) LIMIT 1
            """, new { o.Lat, o.Lon });
        if (stand is null) flags.Add("outside_stand_coverage");

        flags.AddRange(o.Photos.SelectMany(p => p.QualityFlags).Distinct());

        await using var tx = await db.BeginTransactionAsync();
        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO field_observations (user_id, species, geom, gps_accuracy_m, location_source, observed_on, method, outcome, stage, effort_minutes,
                area_searched_m2, amount_litres, plot_count, ripe_count, unripe_count, flower_count, stand_id, area_id, share_aggregated, use_for_training,
                qc_flags, duplicate_of, notes)
            VALUES (@userId, @Species, ST_SetSRID(ST_MakePoint(@Lon, @Lat), 4326), @GpsAccuracyM, @LocationSource, @ObservedOn, @Method, @Outcome, @Stage,
                @EffortMinutes, @AreaSearchedM2, @AmountLitres, @PlotCount, @RipeCount, @UnripeCount, @FlowerCount, @stand, @AreaId, @ShareAggregated,
                @UseForTraining, CAST(@flags AS jsonb), @duplicate, @Notes)
            RETURNING id
            """, new
        {
            userId, o.Species, o.Lat, o.Lon, o.GpsAccuracyM, o.LocationSource, o.ObservedOn, o.Method, o.Outcome, o.Stage, o.EffortMinutes, o.AreaSearchedM2,
            o.AmountLitres, o.PlotCount, o.RipeCount, o.UnripeCount, o.FlowerCount, stand, o.AreaId, o.ShareAggregated, o.UseForTraining,
            flags = JsonSerializer.Serialize(flags), duplicate, Notes = o.Notes is { Length: > 1000 } n ? n[..1000] : o.Notes,
        }, tx);

        foreach (var photo in o.Photos)
        {
            await db.ExecuteAsync("""
                INSERT INTO observation_media (observation_id, asset_uri, mime_type, width, height, bytes, quality_flags)
                VALUES (@id, @AssetUri, @MimeType, @Width, @Height, @Bytes, CAST(@flags AS jsonb))
                """, new { id, photo.AssetUri, photo.MimeType, photo.Width, photo.Height, photo.Bytes, flags = JsonSerializer.Serialize(photo.QualityFlags) }, tx);
        }

        await tx.CommitAsync();
        return new SubmitResult(id, null, flags);
    }

    public async Task<IReadOnlyList<ReviewItem>> ReviewQueueAsync(string status, int limit = 50)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<ReviewItem>("""
            SELECT o.id, o.user_id AS UserId, o.species, o.observed_on AS ObservedOn, o.submitted_at AS SubmittedAt, o.method, o.outcome, o.stage,
                o.effort_minutes AS EffortMinutes, o.area_searched_m2::float8 AS AreaSearchedM2, o.amount_litres::float8 AS AmountLitres, o.plot_count AS PlotCount,
                o.ripe_count AS RipeCount, o.unripe_count AS UnripeCount, o.gps_accuracy_m::float8 AS GpsAccuracyM, o.location_source AS LocationSource,
                o.qc_status AS QcStatus, o.qc_flags::text AS QcFlags, o.qc_note AS QcNote, o.use_for_training AS UseForTraining,
                o.share_aggregated AS ShareAggregated, o.duplicate_of AS DuplicateOf, o.notes, ST_Y(o.geom) AS Lat, ST_X(o.geom) AS Lon,
                (SELECT p.suitability FROM predictions p JOIN model_versions mv ON mv.id = p.model_version_id AND mv.status = 'production'
                    WHERE p.stand_id = o.stand_id AND p.species = o.species LIMIT 1) AS StandSuitability,
                (SELECT count(*)::int FROM field_observations x WHERE x.user_id = o.user_id) AS UserSubmissions,
                (SELECT count(*)::int FROM observation_media m WHERE m.observation_id = o.id) AS Photos
            FROM field_observations o WHERE o.qc_status = @status
            ORDER BY jsonb_array_length(o.qc_flags) DESC, o.submitted_at LIMIT @limit
            """, new { status, limit })).ToList();
    }

    public async Task ReviewAsync(long id, string decision, string? note, string actor)
    {
        if (decision is not ("accepted" or "rejected" or "needs_info" or "pending"))
        {
            throw new ArgumentException("Unknown review decision", nameof(decision));
        }

        await using var db = await openDb();
        await db.ExecuteAsync("UPDATE field_observations SET qc_status = @decision, qc_note = @note, qc_by = @actor, qc_at = now() WHERE id = @id",
            new { id, decision, note, actor });
        await Audit.WriteAsync(db, actor, "observation.review", $"observation:{id}", new { decision, note });
    }

    public async Task<IReadOnlyList<string>> PhotoUrisAsync(long observationId)
    {
        await using var db = await openDb();
        return (await db.QueryAsync<string>("SELECT asset_uri FROM observation_media WHERE observation_id = @observationId", new { observationId })).ToList();
    }

    public async Task<bool> DeleteOwnAsync(string userId, long id)
    {
        await using var db = await openDb();
        return await db.ExecuteAsync("DELETE FROM field_observations WHERE id = @id AND user_id = @userId", new { id, userId }) == 1;
    }
}
