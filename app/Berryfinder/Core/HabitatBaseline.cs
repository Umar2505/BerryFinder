// Model A, version 1: a transparent ecological baseline for berry habitat suitability.
//
// It is NOT a trained or validated model. Each factor maps a recorded stand attribute to a 0..1
// response, shaped by the Finnish forest-berry literature:
//   - Miina, Hotanen & Salo (2009), Silva Fennica 43(4): bilberry cover is highest on mesic (MT) and
//     herb-rich heath (OMT) sites in middle-aged and mature stands and drops sharply after clear-cutting.
//   - Turtiainen, Miina, Salo & Hotanen (2013), Silva Fennica 47(3): lingonberry (cowberry) favours
//     sub-xeric and xeric pine heaths, and its cover and yield fall as stand basal area rises.
// The response shapes are expert judgement on top of those findings, not fitted coefficients, so the
// output is reported as an unvalidated class with its reasons, never as a probability.
public static class HabitatBaseline
{
    public const string Version = "habitat-baseline-1.0";

    public sealed record StandInput(
        string? LandClass,
        string? SiteMainClass,
        int? Fertility,
        int? SoilType,
        string? Drainage,
        string? Development,
        int? MainSpecies,
        double? PropPine,
        double? PropSpruce,
        double? PropOther,
        double? MeanAge,
        double? BasalArea,
        double? MeanHeight,
        DateOnly? MeasurementDate,
        string? DataSourceCode,
        string? SoilClass,
        double? NdviSummer,
        string? RestrictionSeverity);

    public sealed record Driver(string Factor, string Effect, double Weight, double Response, string En, string Fi);

    public sealed record Result(double? Score, string Suitability, string Evidence, bool Excluded, IReadOnlyList<Driver> Drivers);

    private sealed record Factor(string Name, double Weight, double Response, string En, string Fi);

    public static Result Score(string species, StandInput s, DateOnly today)
    {
        var factors = species switch
        {
            Species.Bilberry => BilberryFactors(s),
            Species.Lingonberry => LingonberryFactors(s),
            _ => throw new ArgumentException($"No habitat baseline for '{species}'", nameof(species)),
        };

        var excluded = s.RestrictionSeverity == "exclude";
        var core = factors.Where(f => f.Name is "fertility" or "development" or "age").ToList();

        // Without site fertility or any stand-development information the stand cannot be assessed
        if (!factors.Any(f => f.Name == "fertility") || !core.Any(f => f.Name is "development" or "age"))
        {
            return new Result(null, "not_assessed", "limited", excluded, factors.Select(ToDriver).ToList());
        }

        // Weighted geometric mean: one very poor factor (an open clear-cut, an open bog) pulls the
        // whole stand down, which an arithmetic mean would hide
        var totalWeight = factors.Sum(f => f.Weight);
        var logSum = factors.Sum(f => f.Weight * Math.Log(Math.Max(f.Response, 0.02)));
        var score = Math.Exp(logSum / totalWeight);

        // Land that is not productive forest is downgraded as a whole
        score *= s.LandClass switch { "2" => 0.8, "3" => 0.5, "1" or null => 1.0, _ => 0.3 };

        // Cut-offs are expert judgement on the weighted geometric mean: "high" is reserved for stands near the ecological optimum
        var suitability = score >= 0.88 ? "high" : score >= 0.68 ? "moderate" : score >= 0.45 ? "low" : "unlikely";

        return new Result(Math.Round(score, 4), suitability, Evidence(s, totalWeight, today), excluded,
            factors.Select(ToDriver).OrderByDescending(d => d.Weight * Math.Abs(d.Response - 0.65)).ToList());
    }

    // Evidence quality says how much the class rests on recorded, recent data; it is not a confidence interval
    public static string Evidence(StandInput s, double availableWeight, DateOnly today)
    {
        var points = 0;
        points += availableWeight >= 0.9 ? 2 : availableWeight >= 0.7 ? 1 : 0;

        if (s.MeasurementDate is { } measured)
        {
            var years = (today.DayNumber - measured.DayNumber) / 365.25;
            points += years <= 5 ? 2 : years <= 10 ? 1 : 0;
        }

        if (s.SoilClass is not null and not SoilClassifier.Unmapped)
        {
            points += 1;
        }

        return points >= 4 ? "good" : points >= 2 ? "fair" : "limited";
    }

    private static Driver ToDriver(Factor f) =>
        new(f.Name, f.Response >= 0.8 ? "positive" : f.Response >= 0.5 ? "neutral" : "negative", f.Weight, Math.Round(f.Response, 3), f.En, f.Fi);

    private static List<Factor> BilberryFactors(StandInput s)
    {
        var list = new List<Factor>();

        if (s.Fertility is { } fertility)
        {
            var r = fertility switch { 1 => 0.45, 2 => 0.8, 3 => 1.0, 4 => 0.65, 5 => 0.25, 6 => 0.1, 7 => 0.05, _ => 0.2 };
            var name = ForestCodes.Fertility.GetValueOrDefault(fertility, ("Unknown site", "Tuntematon kasvupaikka"));
            list.Add(new("fertility", 0.30, r,
                r >= 0.8 ? $"{name.Item1}: the site type where bilberry is most abundant" : $"{name.Item1}: bilberry is less common on this site type",
                r >= 0.8 ? $"{name.Item2}: kasvupaikka, jolla mustikkaa on runsaimmin" : $"{name.Item2}: mustikka on tällä kasvupaikalla harvinaisempi"));
        }

        if (s.SiteMainClass is { } main)
        {
            var r = main switch { "1" => 1.0, "2" => 0.7, "3" => 0.35, "4" => 0.05, "5" => 0.05, _ => 0.5 };
            if (r < 1.0)
            {
                list.Add(new("site_main_class", 0.15, r,
                    main == "2" ? "Spruce mire: bilberry grows on hummocks but less densely than on heath" : "Mire site: bilberry is sparse on wet peatland",
                    main == "2" ? "Korpi: mustikkaa mättäillä, mutta harvemmin kuin kankaalla" : "Suokasvupaikka: mustikkaa vähän märällä suolla"));
            }
            else
            {
                list.Add(new("site_main_class", 0.15, r, "Mineral soil heath", "Kangasmaa"));
            }
        }

        AddDevelopment(list, s, Species.Bilberry);

        if (s.BasalArea is { } ba)
        {
            var r = ba < 5 ? 0.4 : ba < 10 ? 0.75 : ba <= 22 ? 1.0 : ba <= 30 ? 0.8 : 0.6;
            list.Add(new("basal_area", 0.15, r,
                r >= 0.8 ? $"Basal area {ba:0} m²/ha: a moderately closed canopy that suits bilberry" : ba < 10 ? $"Basal area {ba:0} m²/ha: open canopy, bilberry suffers from exposure" : $"Basal area {ba:0} m²/ha: dense canopy shades the field layer",
                r >= 0.8 ? $"Pohjapinta-ala {ba:0} m²/ha: sopivan suljettu latvusto mustikalle" : ba < 10 ? $"Pohjapinta-ala {ba:0} m²/ha: avoin latvusto, mustikka kärsii" : $"Pohjapinta-ala {ba:0} m²/ha: tiheä latvusto varjostaa"));
        }

        if (SpeciesMix(s) is { } mix)
        {
            var r = mix switch { "spruce" => 0.95, "pine" => 0.85, "mixed_conifer" => 1.0, _ => 0.6 };
            list.Add(new("tree_species", 0.10, r,
                mix == "broadleaf" ? "Broadleaf-dominated stand: bilberry usually sparser" : "Conifer-dominated stand typical of bilberry forests",
                mix == "broadleaf" ? "Lehtipuuvaltainen metsikkö: mustikkaa yleensä vähemmän" : "Havupuuvaltainen metsikkö, tyypillinen mustikkametsä"));
        }

        AddSoil(list, s, new Dictionary<string, double>
        {
            [SoilClassifier.ThickPeat] = 0.3, [SoilClassifier.ThinPeat] = 0.65, [SoilClassifier.Till] = 1.0,
            [SoilClassifier.Coarse] = 0.7, [SoilClassifier.Fine] = 0.75, [SoilClassifier.Rock] = 0.4,
        });

        AddNdvi(list, s);
        return list;
    }

    private static List<Factor> LingonberryFactors(StandInput s)
    {
        var list = new List<Factor>();

        if (s.Fertility is { } fertility)
        {
            var r = fertility switch { 1 => 0.15, 2 => 0.35, 3 => 0.75, 4 => 1.0, 5 => 0.9, 6 => 0.55, 7 => 0.45, _ => 0.4 };
            var name = ForestCodes.Fertility.GetValueOrDefault(fertility, ("Unknown site", "Tuntematon kasvupaikka"));
            list.Add(new("fertility", 0.30, r,
                r >= 0.8 ? $"{name.Item1}: the drier, less fertile heath where lingonberry thrives" : $"{name.Item1}: lingonberry is less abundant on this site type",
                r >= 0.8 ? $"{name.Item2}: kuivahko ja karumpi kangas, jolla puolukka viihtyy" : $"{name.Item2}: puolukkaa on tällä kasvupaikalla vähemmän"));
        }

        if (s.SiteMainClass is { } main)
        {
            var r = main switch { "1" => 1.0, "2" => 0.3, "3" => 0.55, "4" => 0.05, "5" => 0.05, _ => 0.5 };
            list.Add(new("site_main_class", 0.15, r,
                main == "1" ? "Mineral soil heath" : main == "3" ? "Pine mire: lingonberry on drier hummocks" : "Wet mire site: little lingonberry",
                main == "1" ? "Kangasmaa" : main == "3" ? "Räme: puolukkaa kuivemmilla mättäillä" : "Märkä suokasvupaikka: vähän puolukkaa"));
        }

        AddDevelopment(list, s, Species.Lingonberry);

        if (s.BasalArea is { } ba)
        {
            var r = ba < 5 ? 0.75 : ba <= 15 ? 1.0 : ba <= 22 ? 0.8 : ba <= 30 ? 0.55 : 0.4;
            list.Add(new("basal_area", 0.15, r,
                r >= 0.8 ? $"Basal area {ba:0} m²/ha: a fairly open canopy where lingonberry yields well" : $"Basal area {ba:0} m²/ha: dense canopy reduces lingonberry yield",
                r >= 0.8 ? $"Pohjapinta-ala {ba:0} m²/ha: melko avoin latvusto, puolukka satoaa hyvin" : $"Pohjapinta-ala {ba:0} m²/ha: tiheä latvusto heikentää puolukkasatoa"));
        }

        if (SpeciesMix(s) is { } mix)
        {
            var r = mix switch { "pine" => 1.0, "mixed_conifer" => 0.8, "spruce" => 0.45, _ => 0.5 };
            list.Add(new("tree_species", 0.10, r,
                mix == "pine" ? "Pine-dominated stand, typical of lingonberry heaths" : mix == "spruce" ? "Spruce-dominated stand: shade limits lingonberry" : "Mixed or broadleaf stand",
                mix == "pine" ? "Mäntyvaltainen metsikkö, tyypillinen puolukkakangas" : mix == "spruce" ? "Kuusivaltainen metsikkö: varjo rajoittaa puolukkaa" : "Seka- tai lehtipuumetsikkö"));
        }

        AddSoil(list, s, new Dictionary<string, double>
        {
            [SoilClassifier.ThickPeat] = 0.35, [SoilClassifier.ThinPeat] = 0.6, [SoilClassifier.Till] = 0.9,
            [SoilClassifier.Coarse] = 1.0, [SoilClassifier.Fine] = 0.6, [SoilClassifier.Rock] = 0.7,
        });

        AddNdvi(list, s);
        return list;
    }

    private static void AddDevelopment(List<Factor> list, StandInput s, string species)
    {
        var bilberry = species == Species.Bilberry;

        if (s.Development is { } dev && ForestCodes.Development.ContainsKey(dev))
        {
            var r = (dev, bilberry) switch
            {
                ("A0", true) => 0.12, ("A0", false) => 0.35,
                ("S0", true) => 0.2, ("S0", false) => 0.45,
                ("T1", true) => 0.2, ("T1", false) => 0.45,
                ("T2", true) => 0.35, ("T2", false) => 0.6,
                ("Y1", true) => 0.45, ("Y1", false) => 0.6,
                ("02", true) => 0.65, ("02", false) => 0.9,
                ("03", _) => bilberry ? 0.9 : 1.0,
                ("04", _) => bilberry ? 1.0 : 0.85,
                ("05", _) => bilberry ? 0.6 : 0.7,
                ("ER", _) => 0.9,
                _ => 0.6,
            };
            var label = ForestCodes.Development[dev];
            var recentlyHarvested = dev is "A0" or "S0" or "T1";
            list.Add(new("development", 0.20, r,
                recentlyHarvested ? $"{label.En}: recently harvested; berry plants take decades to recover" : $"{label.En}",
                recentlyHarvested ? $"{label.Fi}: äskettäin hakattu; varvut toipuvat vuosikymmenissä" : $"{label.Fi}"));
            return;
        }

        if (s.MeanAge is { } age)
        {
            var r = bilberry
                ? age < 15 ? 0.2 : age < 30 ? 0.4 : age < 50 ? 0.7 : age < 80 ? 0.9 : 1.0
                : age < 15 ? 0.45 : age < 30 ? 0.65 : age < 80 ? 1.0 : 0.85;
            list.Add(new("age", 0.20, r, $"Mean tree age {age:0} years", $"Puuston keski-ikä {age:0} vuotta"));
        }
    }

    private static void AddSoil(List<Factor> list, StandInput s, Dictionary<string, double> responses)
    {
        if (s.SoilClass is { } soil && responses.TryGetValue(soil, out var r))
        {
            var label = SoilClassifier.Labels[soil];
            list.Add(new("soil", 0.10, r, $"Surface deposit (GTK): {label.En}", $"Pintamaalaji (GTK): {label.Fi}"));
        }
    }

    private static void AddNdvi(List<Factor> list, StandInput s)
    {
        if (s.NdviSummer is { } ndvi)
        {
            var r = ndvi < 0.3 ? 0.3 : ndvi < 0.5 ? 0.7 : ndvi <= 0.85 ? 1.0 : 0.85;
            list.Add(new("ndvi", 0.10, r, $"Summer Sentinel-2 NDVI {ndvi:0.00}", $"Kesän Sentinel-2 NDVI {ndvi:0.00}"));
        }
    }

    public static string? SpeciesMix(StandInput s)
    {
        if (s.PropPine is { } pine && s.PropSpruce is { } spruce && s.PropOther is { } other && pine + spruce + other > 0.1)
        {
            if (other >= 0.5)
            {
                return "broadleaf";
            }

            if (pine >= 0.65)
            {
                return "pine";
            }

            return spruce >= 0.65 ? "spruce" : "mixed_conifer";
        }

        if (ForestCodes.IsPine(s.MainSpecies))
        {
            return "pine";
        }

        if (ForestCodes.IsSpruce(s.MainSpecies))
        {
            return "spruce";
        }

        return ForestCodes.IsBroadleaf(s.MainSpecies) ? "broadleaf" : null;
    }
}

public static class Species
{
    public const string Bilberry = "bilberry";
    public const string Lingonberry = "lingonberry";
    public const string Cloudberry = "cloudberry";

    public static readonly string[] Supported = [Bilberry, Lingonberry];
}
