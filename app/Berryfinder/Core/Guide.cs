// The "where to go and what to look for" logic: turns ranked picking areas into a few plain recommendations.
// Pure functions, no database or network, so they are unit-tested.
public enum Odds
{
    LongShot = 1,
    WorthATry = 2,
    Promising = 3,
}

public sealed record Candidate(long AreaId, string Species, double Lat, double Lon, double? AccessLat, double? AccessLon,
    string Suitability, string Evidence, double MeanScore, double AreaHa, double? DistRoadM);

public sealed record Ranked(Candidate Candidate, double? DriveMinutes, string Availability, double Utility);

public sealed record HabitatInfo(string Forest, string Age, string Ground);

public static class Guide
{
    // Weaker evidence (the grid model, or stand data with key records missing) counts for less than the same score on better maps
    public static double Quality(Candidate c) =>
        (c.Suitability == "high" ? 1.0 : 0.6) * Math.Min(1.0, Math.Sqrt(Math.Max(c.AreaHa, 0) / 5.0)) * (0.5 + 0.5 * Math.Clamp(c.MeanScore, 0, 1))
        * (c.Evidence == "limited" ? 0.8 : 1.0);

    // Evidence says how complete and recent the underlying maps are; it can lower the odds, never raise them past the suitability
    public static Odds OddsOf(string suitability, string evidence)
    {
        var level = suitability == "high" ? 3 : suitability == "moderate" ? 2 : 1;
        if (evidence == "limited")
        {
            level--;
        }

        return (Odds)Math.Clamp(level, 1, 3);
    }

    // How ready the season is where the user is: a berry that is ripe now beats one that has just begun or is nearly over
    public static double StageFactor(string availability) => availability switch
    {
        "peak" => 1.0,
        "starting" => 0.7,
        "late" => 0.65,
        "unknown" => 0.6,
        _ => 0.0,
    };

    // Time matters a lot to someone deciding where to go: roughly halves the appeal every 30 minutes of driving
    public static double Utility(Candidate c, double driveMinutes, string availability) =>
        Quality(c) * Math.Exp(-driveMinutes / 43.0) * StageFactor(availability);

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }

    // Best first, skipping places closer than minSeparationKm to one already chosen, so three suggestions are three different outings.
    // While other berries are in season, one berry takes at most maxPerSpecies places, so the list also says what else to look for;
    // the best place always comes first, and empty places are filled when nothing else qualifies.
    public static IReadOnlyList<Ranked> PickTop(IEnumerable<Ranked> options, int take, double minSeparationKm = 0.8, int maxPerSpecies = 2)
    {
        var ordered = options.Where(o => o.Utility > 0).OrderByDescending(o => o.Utility).ToList();
        var chosen = new List<Ranked>();
        foreach (var perSpecies in new[] { maxPerSpecies, int.MaxValue })
        {
            foreach (var option in ordered)
            {
                if (chosen.Count == take)
                {
                    break;
                }

                if (chosen.Contains(option)
                    || chosen.Count(c => c.Candidate.Species == option.Candidate.Species) >= perSpecies
                    || chosen.Any(c => HaversineKm(c.Candidate.Lat, c.Candidate.Lon, option.Candidate.Lat, option.Candidate.Lon) < minSeparationKm))
                {
                    continue;
                }

                chosen.Add(option);
            }
        }

        return chosen.OrderByDescending(c => c.Utility).ToList();
    }

    // Walking allowance from the road: straight-line distance, a terrain factor and a walking pace. Never shown as a route.
    public static int WalkMinutes(double? distRoadM) => distRoadM is null ? 0 : (int)Math.Round((distRoadM.Value * 1.5 / 1000.0) / 4.0 * 60.0);

    public static string WalkText(double? distRoadM, string lang)
    {
        var minutes = WalkMinutes(distRoadM);
        return minutes < 2 ? (lang == "fi" ? "Aivan tien vieressä" : "Right by the road") : lang == "fi" ? $"{minutes} min kävelyä" : $"{minutes} min walk";
    }

    public static string DriveText(double? minutes, string lang) => minutes is null
        ? (lang == "fi" ? "Ajoaika ei saatavilla" : "Drive time unavailable")
        : lang == "fi" ? $"{Math.Max(1, (int)Math.Round(minutes.Value))} min ajomatka" : $"{Math.Max(1, (int)Math.Round(minutes.Value))} min drive";

    public static string OddsLabel(Odds odds, string lang) => odds switch
    {
        Odds.Promising => lang == "fi" ? "Lupaava" : "Promising",
        Odds.WorthATry => lang == "fi" ? "Kokeilemisen arvoinen" : "Worth a try",
        _ => lang == "fi" ? "Epävarma" : "Long shot",
    };

    public static string StageText(string availability, string lang) => availability switch
    {
        "peak" => lang == "fi" ? "Kausi parhaimmillaan" : "Season is at its best",
        "starting" => lang == "fi" ? "Kausi on vasta alkamassa" : "Season is just starting",
        "late" => lang == "fi" ? "Kauden loppupuoli" : "Late in the season",
        _ => lang == "fi" ? "Sesongissa" : "In season",
    };

    // Codes first, words second: the same description serves both languages and the JSON API
    public static HabitatInfo HabitatOf(double? pine, double? spruce, double? broadleaf, double? meanAge, int? fertility)
    {
        var forest = pine is >= 0.6 ? "pine" : spruce is >= 0.6 ? "spruce" : broadleaf is >= 0.5 ? "broadleaf" : "mixed";
        var age = meanAge is null ? "" : meanAge >= 70 ? "mature" : meanAge >= 40 ? "middle" : "young";
        var ground = fertility switch { 2 => "rich", 3 => "moist", 4 => "dry", 5 or 6 => "sandy", _ => "" };
        return new HabitatInfo(forest, age, ground);
    }

    // What the grid model saw, averaged over an area: Finnish stand data where it exists, satellite land cover and SoilGrids elsewhere
    public static HabitatInfo HabitatFromGrid(IReadOnlyDictionary<string, double> h)
    {
        double? V(string key) => h.TryGetValue(key, out var v) ? v : null;

        var tree = V("lc_tree") ?? 0;
        var mire = (V("mire") ?? 0) >= 0.5;                               // share of the area's cells on mire (ModelGate.IsMire)
        int? fertility = V("fi_fertility") is { } f ? (int)Math.Round(f) : null;

        if (mire)
        {
            return new HabitatInfo(tree >= 0.4 ? "wooded_mire" : "bog", "", (V("soc") ?? 0) >= 150 ? "peat" : "");
        }

        if (tree < 0.3)
        {
            return new HabitatInfo("open", "", "");
        }

        if (tree < 0.6)
        {
            return new HabitatInfo("edge", "", "");
        }

        var forest = HabitatOf(V("fi_prop_pine"), V("fi_prop_spruce"), V("fi_prop_other"), V("fi_age"), fertility);
        return forest;
    }

    public static string Describe(HabitatInfo h, string lang)
    {
        var fi = lang == "fi";
        var forest = h.Forest switch
        {
            "pine" => fi ? "mäntymetsä" : "pine forest",
            "spruce" => fi ? "kuusimetsä" : "spruce forest",
            "broadleaf" => fi ? "lehtimetsä" : "broadleaf forest",
            "bog" => fi ? "avosuo" : "open bog",
            "wooded_mire" => fi ? "puustoinen suo" : "wooded bog",
            "open" => fi ? "avoin maasto" : "open ground",
            "edge" => fi ? "metsänreuna tai aukko" : "forest edge or clearing",
            _ => fi ? "sekametsä" : "mixed forest",
        };
        var age = h.Age switch
        {
            "mature" => fi ? "Varttunut" : "Mature",
            "middle" => fi ? "Keski-ikäinen" : "Middle-aged",
            "young" => fi ? "Nuori" : "Young",
            _ => "",
        };
        var ground = h.Ground switch
        {
            "rich" => fi ? "rehevä maa" : "rich ground",
            "moist" => fi ? "tuore maa" : "moist ground",
            "dry" => fi ? "kuiva maa" : "dry ground",
            "sandy" => fi ? "hiekkainen kuiva maa" : "dry, sandy ground",
            "peat" => fi ? "turvemaa" : "peaty ground",
            _ => "",
        };

        var first = age.Length > 0 ? $"{age} {forest}" : char.ToUpperInvariant(forest[0]) + forest[1..];
        return ground.Length > 0 ? $"{first} · {ground}" : first;
    }
}
