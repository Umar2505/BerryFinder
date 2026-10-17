using System.Globalization;
using System.Text.Json;

// The Nordic habitat model (notebooks/nordic_berry_habitat_model.ipynb) is a PyTorch ensemble trained offline. The app never
// runs the network: notebooks/predict_region.py scores a grid, and the app imports that grid plus the model card that records
// how the run was evaluated. data/ml/ holds both, so a deploy carries them.
// Rules* are set only for a model trained on rule-based pseudo-labels; Gbif* is always the check against real held-out sightings
public sealed record ModelCardSpecies(double? RulesAuc, double? RulesBoyce, double GbifAuc, double GbifBoyce, double GbifRecallTop20, int GbifPresences);

public sealed record ModelCard(string Version, string Run, string TrainedOn, string LabelMode, string GridFile, double GridStepM, string ModelFile,
    string ModelSha256, int TrainingRows, IReadOnlyList<double> ValidationAucMembers, IReadOnlyDictionary<string, ModelCardSpecies> Species)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ModelCard Parse(byte[] json) => JsonSerializer.Deserialize<ModelCard>(json, Json) ?? throw new InvalidDataException("Empty model card");
}

// Which species get spots from the grid model, and how its cells become picking areas. Pure, so it is unit-tested.
public static class ModelGate
{
    public const string Kind = "pytorch_ensemble";

    // Clearly better than chance (AUC 0.5) at ranking real GBIF sightings above the plant background, with a positive Boyce index
    public const double MinGbifAuc = 0.55;

    // Suitability index: the share of the model's Nordic training background that scores lower. 90 = top 10 %.
    public const double MinIndex = 90;
    public const double HighIndex = 97;

    // At most the best 10 % of the region's land per berry becomes spots, the best 3 % "very promising" ground, so the map
    // points somewhere instead of colouring half the region
    public const double RegionalShare = 0.10;
    public const double RegionalHighShare = 0.03;

    // The labels were written rules and the check is presence-only, so a grid area is never better than "limited" evidence:
    // "Worth a try" at most, never "Promising"
    public const string Evidence = "limited";

    public static (string Status, string Notes) Decide(ModelCardSpecies m, bool hasStandBaseline)
    {
        var auc = m.GbifAuc.ToString("0.00", CultureInfo.InvariantCulture);
        if (m.GbifAuc < MinGbifAuc || m.GbifBoyce <= 0)
        {
            return ("rejected", $"Ranks real GBIF sightings no better than chance (AUC {auc}), so it is not used for spots.");
        }

        return hasStandBaseline
            ? ("candidate", $"Agrees with real GBIF sightings at AUC {auc} (0.5 = chance). The forest-stand baseline stays in production for this berry.")
            : ("production", $"Agrees with real GBIF sightings at AUC {auc} (0.5 = chance). Trained on written habitat rules, not checked in the field.");
    }


    // The berry's basic habitat as the app describes it ("open bogs", "clearings and edges"), from the Finnish stand data where
    // it exists and satellite land cover elsewhere. The model learned written rules on coarse soil maps; without this check it
    // ranks dense forest on carbon-rich SoilGrids soil as cloudberry bog (most of its top Oulu cells were mineral-soil forest).
    public static bool InHabitat(string species, IReadOnlyDictionary<string, double> h)
    {
        double? V(string key) => h.TryGetValue(key, out var v) ? v : null;

        if ((V("lc_built") ?? 0) >= 0.3 || (V("lc_crop") ?? 0) >= 0.5)
        {
            return false;      // towns, yards and fields
        }

        var mire = IsMire(h);
        var tree = V("lc_tree") ?? 0;

        return species switch
        {
            "cloudberry" or "cranberry" or "bog_bilberry" => mire,
            "raspberry" or "wild_strawberry" => tree <= 0.7 && !mire,         // clearings, edges and meadows
            "arctic_bramble" => (V("lc_wetland") ?? 0) + (V("lc_grass") ?? 0) >= 0.15,
            "sea_buckthorn" => tree <= 0.3,                                     // open shore
            "bilberry" or "lingonberry" => tree >= 0.4,                        // forest floor
            _ => true,
        };
    }

    // Finnish site class 2 to 5 (spruce mire, pine mire, open bog, fen); without stand data, wetland in the satellite land cover
    public static bool IsMire(IReadOnlyDictionary<string, double> h)
    {
        var site = h.TryGetValue("fi_site_main", out var s) ? s : (double?)null;
        return site is >= 2 and <= 5 || (site is null && h.TryGetValue("lc_wetland", out var w) && w >= 0.2);
    }

    // Cells that become spots for one berry: in its habitat, in the top of the Nordic background, then the region's best by the
    // model's probability. A cell is "high" when it is also among the region's very best.
    public static IReadOnlyList<PickedCell> Select(IReadOnlyList<GridCell> cells, string species)
    {
        var land = cells.Where(c => !c.Ood && c.Scores.ContainsKey(species)).ToList();
        var keep = (int)Math.Floor(land.Count * RegionalShare);
        var high = (int)Math.Floor(land.Count * RegionalHighShare);

        return land
            .Where(c => c.Scores[species].Index >= MinIndex && InHabitat(species, c.Habitat))
            .OrderByDescending(c => c.Scores[species].Probability)
            .ThenBy(c => c.X).ThenBy(c => c.Y)
            .Take(keep)
            .Select((c, rank) => new PickedCell(c.X, c.Y, rank < high && c.Scores[species].Index >= HighIndex, IsMire(c.Habitat)))
            .ToList();
    }
}

public sealed record PickedCell(int X, int Y, bool High, bool Mire);

public sealed record GridScore(double Index, double Probability, double Spread, double? Rule);

public sealed record GridCell(int X, int Y, bool Ood, IReadOnlyDictionary<string, GridScore> Scores, IReadOnlyDictionary<string, double> Habitat)
{
    // From the jsonb columns of model_grid_cells
    public static GridCell FromJson(int x, int y, bool ood, string scoresJson, string habitatJson)
    {
        var scores = new Dictionary<string, GridScore>();
        using (var doc = JsonDocument.Parse(scoresJson))
        {
            foreach (var sp in doc.RootElement.EnumerateObject())
            {
                double? Get(string k) => sp.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                if (Get("idx") is { } idx && Get("p") is { } p)
                {
                    scores[sp.Name] = new GridScore(idx, p, Get("sd") ?? 0, Get("rule"));
                }
            }
        }

        var habitat = new Dictionary<string, double>();
        using (var doc = JsonDocument.Parse(habitatJson))
        {
            foreach (var kv in doc.RootElement.EnumerateObject())
            {
                if (kv.Value.ValueKind == JsonValueKind.Number)
                {
                    habitat[kv.Name] = kv.Value.GetDouble();
                }
            }
        }

        return new GridCell(x, y, ood, scores, habitat);
    }
}

// Reads the CSV written by notebooks/predict_region.py: one row per land cell, idx_/p_/sd_/rule_ columns per species
public static class GridCsv
{
    public static readonly string[] HabitatColumns =
    [
        "lc_tree", "lc_shrub", "lc_grass", "lc_crop", "lc_built", "lc_water", "lc_wetland", "lc_moss", "ph", "soc", "sand",
        "elevation", "built_dist_m", "fi_fertility", "fi_site_main", "fi_prop_pine", "fi_prop_spruce", "fi_prop_other", "fi_age",
    ];

    public static IReadOnlyList<GridCell> Parse(TextReader reader, IReadOnlyCollection<string> species)
    {
        var header = (reader.ReadLine() ?? throw new InvalidDataException("The grid file is empty")).Split(',');
        var col = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);

        int Need(string name) => col.TryGetValue(name, out var i) ? i : throw new InvalidDataException($"The grid file has no '{name}' column");

        var x = Need("cell_x");
        var y = Need("cell_y");
        var ood = Need("ood");
        var perSpecies = species.Select(s => (s, idx: Need($"idx_{s}"), p: Need($"p_{s}"), sd: Need($"sd_{s}"), rule: col.GetValueOrDefault($"rule_{s}", -1))).ToList();
        var habitat = HabitatColumns.Where(col.ContainsKey).Select(c => (c, i: col[c])).ToList();

        var cells = new List<GridCell>();
        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split(',');
            if (f.Length != header.Length)
            {
                throw new InvalidDataException($"Grid line {lineNo} has {f.Length} fields, expected {header.Length}");
            }

            var scores = new Dictionary<string, GridScore>();
            foreach (var (s, idx, p, sd, rule) in perSpecies)
            {
                if (Num(f[idx]) is { } index && Num(f[p]) is { } prob)
                {
                    scores[s] = new GridScore(index, prob, Num(f[sd]) ?? 0, rule >= 0 ? Num(f[rule]) : null);
                }
            }

            var values = new Dictionary<string, double>();
            foreach (var (c, i) in habitat)
            {
                if (Num(f[i]) is { } v)
                {
                    values[c] = v;
                }
            }

            cells.Add(new GridCell((int)Math.Round(Num(f[x]) ?? throw Bad("cell_x")), (int)Math.Round(Num(f[y]) ?? throw Bad("cell_y")),
                Num(f[ood]) is > 0, scores, values));

            InvalidDataException Bad(string what) => new($"Grid line {lineNo} has no {what}");
        }

        return cells;
    }

    private static double? Num(string s) =>
        s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
