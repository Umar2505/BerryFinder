using System.Collections.Concurrent;

// Source is "stands" (forest-stand maps) or "grid_model" (the Nordic machine-learning model's 250 m grid)
public sealed record GuidePick(long AreaId, string Species, string Place, double Lat, double Lon, double NavLat, double NavLon,
    double? DriveMinutes, double? DistRoadM, double AreaHa, int Odds, string Availability, HabitatInfo Habitat, string Source = "stands");

// State is "ready" (picks available), "off_season" (nothing is ripe here now) or "no_spots" (outside the area the app covers)
public sealed record GuideResult(string State, DateOnly Date, GuidePick? Hero, IReadOnlyList<GuidePick> Alternatives,
    IReadOnlyList<string> InSeason, string? NextSpecies, int? DaysUntilNext)
{
    public IReadOnlyList<GuidePick> All => Hero is null ? [] : [Hero, .. Alternatives];
}

// Turns "I am here, today" into at most three plain recommendations: where to go, which berry, how far, how good.
// Ranking weighs how good the forest is, how ripe the season is, and how long the drive takes.
public sealed class GuideService(Queries queries, PredictionPublisher publisher)
{
    private const double SearchRadiusKm = 40;
    private readonly ConcurrentDictionary<string, (DateTime At, GuideResult Result)> _cache = new();

    public async Task<GuideResult> GetAsync(double lat, double lon, DateOnly today, CancellationToken ct = default)
    {
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{lat:0.00}:{lon:0.00}:{today:yyyyMMdd}");
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
        {
            return hit.Result;
        }

        var result = await ComputeAsync(lat, lon, today, ct);
        if (_cache.Count > 5000)
        {
            _cache.Clear();
        }

        _cache[key] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<GuideResult> ComputeAsync(double lat, double lon, DateOnly today, CancellationToken ct)
    {
        var inSeason = BerryCatalog.All.Where(b => BerryCatalog.InSeason(b, today)).Select(b => b.Id).ToList();

        // Season stage from the weather where we have it; where we do not, the calendar window decides
        var weather = await queries.WeatherNearAsync(lat, lon, today.Year);
        var grid = await queries.GridWeatherAsync(lat, lon, today.Year);
        // Every berry with a production model: the stand baselines, and the grid model where it passed its check
        var mapped = BerryCatalog.All.Select(b => b.Id).Where(publisher.Production.ContainsKey).ToList();
        var availability = new Dictionary<string, string>();
        foreach (var species in mapped)
        {
            var estimate = Phenology.Estimate(species, today, weather?.Days ?? [], weather?.NormalJunJulPrecip, grid);
            availability[species] = estimate.Availability == "unknown"
                ? (BerryCatalog.InSeason(BerryCatalog.Get(species), today) ? "unknown" : "over")
                : estimate.Availability;
        }

        var active = mapped.Where(s => availability[s] is "starting" or "peak" or "late" or "unknown").ToList();
        if (active.Count == 0)
        {
            var next = BerryCatalog.All.OrderBy(b => BerryCatalog.DaysUntilStart(b, today)).First();
            return new GuideResult("off_season", today, null, [], inSeason, next.Id, BerryCatalog.DaysUntilStart(next, today));
        }

        var candidates = new List<(AreaSummary Area, string Species)>();
        foreach (var species in active)
        {
            foreach (var area in await queries.SearchAreasAsync(species, lat, lon, SearchRadiusKm, false, null, true, 12))
            {
                candidates.Add((area, species));
            }
        }

        if (candidates.Count == 0)
        {
            return new GuideResult("no_spots", today, null, [], inSeason, null, null);
        }

        static Candidate ToCandidate(AreaSummary a) => new(a.Id, a.Species, a.Lat, a.Lon, a.AccessLat, a.AccessLon, a.Suitability,
            a.PredictionKind == ModelGate.Kind ? ModelGate.Evidence : "fair", a.MeanScore, a.AreaHa, a.DistRoadM);

        // First pass with a rough drive estimate (about 35 km/h door to door) to choose who gets a real routing request:
        // the best few of each berry, so the final picks can offer more than one berry
        var shortlist = candidates
            .Select(c => (c.Area, c.Species, Candidate: ToCandidate(c.Area), Rough: Guide.Utility(ToCandidate(c.Area), c.Area.DistanceKm * 1.7, availability[c.Species])))
            .GroupBy(c => c.Species)
            .SelectMany(g => g.OrderByDescending(c => c.Rough).Take(3))
            .OrderByDescending(c => c.Rough)
            .Take(10).ToList();

        var targets = shortlist.Select(c => c.Candidate.AccessLat is { } al && c.Candidate.AccessLon is { } ao ? (al, ao) : (c.Candidate.Lat, c.Candidate.Lon)).ToList();
        var drives = await Router.DriveTimesAsync((lat, lon), targets, ct);

        // A missing drive time is only estimated for ordering; the screen then says it is unavailable
        var ranked = shortlist.Select((c, i) =>
        {
            var minutes = drives.Count > i ? drives[i]?.Minutes : null;
            var effective = minutes ?? c.Area.DistanceKm * 1.7;
            return new Ranked(c.Candidate, minutes, availability[c.Species], Guide.Utility(c.Candidate, effective, availability[c.Species]));
        }).ToList();

        var picks = Guide.PickTop(ranked, 3);
        var built = new List<GuidePick>();
        foreach (var pick in picks)
        {
            built.Add(await BuildPickAsync(pick, lat, lon));
        }

        return built.Count == 0
            ? new GuideResult("no_spots", today, null, [], inSeason, null, null)
            : new GuideResult("ready", today, built[0], built.Skip(1).ToList(), inSeason, null, null);
    }

    private async Task<GuidePick> BuildPickAsync(Ranked pick, double userLat, double userLon)
    {
        var c = pick.Candidate;
        var navLat = c.AccessLat ?? c.Lat;
        var navLon = c.AccessLon ?? c.Lon;
        var detail = await queries.AreaDetailAsync(c.AreaId, userLat, userLon);

        var habitat = new HabitatInfo("mixed", "", "");
        var evidence = c.Evidence;
        if (detail?.Model is { } model)
        {
            habitat = Guide.HabitatFromGrid(model.Habitat);
            evidence = ModelGate.Evidence;
        }
        else if (detail is not null)
        {
            int? fertility = detail.Fertility.Count > 0 && int.TryParse(detail.Fertility[0].Key, out var f) ? f : null;
            habitat = Guide.HabitatOf(detail.PropPine, detail.PropSpruce, detail.PropOther, detail.MeanAge, fertility);
            evidence = detail.Evidence.Count > 0 ? detail.Evidence[0].Key : "fair";
        }

        var place = await ReverseGeocoder.NameAsync(navLat, navLon) ?? "";
        return new GuidePick(c.AreaId, c.Species, place, c.Lat, c.Lon, navLat, navLon, pick.DriveMinutes, c.DistRoadM, c.AreaHa,
            (int)Guide.OddsOf(c.Suitability, evidence), pick.Availability, habitat, detail?.Model is null ? "stands" : "grid_model");
    }
}
