using System.IO.Compression;
using Xunit;

// The grid model's gate, cell selection and file formats. Cells below are SYNTHETIC fixtures, except where the shipped files are read.
public class GridModelTests
{
    private static ModelCardSpecies Check(double gbifAuc, double gbifBoyce = 0.5) => new(0.8, 0.5, gbifAuc, gbifBoyce, 0.3, 400);

    private static GridCell Cell(int x, double index, double p, Dictionary<string, double>? habitat = null, bool ood = false, string species = "cloudberry") =>
        new(x, 0, ood, new Dictionary<string, GridScore> { [species] = new(index, p, 0.2, 0.3) }, habitat ?? Bog());

    private static Dictionary<string, double> Bog() => new() { ["fi_site_main"] = 3, ["lc_tree"] = 0.5, ["lc_wetland"] = 0.1 };

    private static Dictionary<string, double> MineralForest() => new() { ["fi_site_main"] = 1, ["lc_tree"] = 0.95, ["lc_wetland"] = 0 };

    [Fact]
    public void A_berry_without_a_stand_baseline_goes_to_production_when_it_beats_chance()
    {
        Assert.Equal("production", ModelGate.Decide(Check(0.65), hasStandBaseline: false).Status);
    }

    [Fact]
    public void A_berry_with_a_stand_baseline_keeps_it_and_the_grid_model_waits_as_a_candidate()
    {
        Assert.Equal("candidate", ModelGate.Decide(Check(0.65), hasStandBaseline: true).Status);
    }

    [Theory]
    [InlineData(0.437, -0.498)]   // the run's bilberry: worse than chance
    [InlineData(0.501, -0.019)]   // wild strawberry: chance
    [InlineData(0.60, -0.1)]      // a fair AUC with a negative Boyce index is still rejected
    public void A_model_that_does_not_beat_chance_on_real_sightings_is_rejected(double auc, double boyce)
    {
        var (status, notes) = ModelGate.Decide(Check(auc, boyce), hasStandBaseline: false);
        Assert.Equal("rejected", status);
        Assert.Contains("chance", notes);
    }

    [Fact]
    public void Bog_berries_need_a_mire_by_stand_data_or_wetland_where_stand_data_is_missing()
    {
        Assert.True(ModelGate.InHabitat("cloudberry", Bog()));
        Assert.False(ModelGate.InHabitat("cloudberry", MineralForest()));
        Assert.True(ModelGate.InHabitat("cranberry", new Dictionary<string, double> { ["lc_wetland"] = 0.4 }));
        Assert.False(ModelGate.InHabitat("cranberry", new Dictionary<string, double> { ["lc_wetland"] = 0.05 }));
    }

    [Fact]
    public void Nobody_is_sent_into_a_town_or_a_field()
    {
        var yard = new Dictionary<string, double> { ["lc_tree"] = 0.5, ["lc_built"] = 0.4 };
        var field = new Dictionary<string, double> { ["lc_tree"] = 0.1, ["lc_crop"] = 0.8 };
        foreach (var species in new[] { "raspberry", "sea_buckthorn", "arctic_bramble", "bilberry" })
        {
            Assert.False(ModelGate.InHabitat(species, yard), species);
            Assert.False(ModelGate.InHabitat(species, field), species);
        }
    }

    [Fact]
    public void Selection_keeps_the_regions_best_tenth_inside_the_habitat()
    {
        // 100 land cells: 30 bog cells that all pass, 70 forest cells that score even higher but are the wrong habitat
        var cells = Enumerable.Range(0, 30).Select(i => Cell(i, 95, 0.30 + i * 0.001))
            .Concat(Enumerable.Range(100, 70).Select(i => Cell(i, 99, 0.60, MineralForest())))
            .ToList();

        var picked = ModelGate.Select(cells, "cloudberry");

        Assert.Equal(10, picked.Count);                                         // 10 % of 100 land cells
        Assert.All(picked, p => Assert.InRange(p.X, 0, 29));                     // only bog cells
        Assert.Equal(Enumerable.Range(20, 10).Reverse(), picked.Select(p => p.X)); // the highest probabilities first
        Assert.All(picked, p => Assert.True(p.Mire));
    }

    [Fact]
    public void Selection_needs_the_top_of_the_nordic_background_and_skips_unusual_places()
    {
        var cells = new List<GridCell> { Cell(1, 89.9, 0.9), Cell(2, 99, 0.9, ood: true) };
        cells.AddRange(Enumerable.Range(10, 18).Select(i => Cell(i, 50, 0.01)));
        Assert.Empty(ModelGate.Select(cells, "cloudberry"));
    }

    [Fact]
    public void Only_the_very_best_cells_are_marked_high()
    {
        var cells = Enumerable.Range(0, 200).Select(i => Cell(i, i < 3 ? 99 : 96, 0.9 - i * 0.001)).ToList();
        var picked = ModelGate.Select(cells, "cloudberry");
        Assert.Equal(20, picked.Count);
        Assert.Equal([0, 1, 2], picked.Where(p => p.High).Select(p => p.X));    // top 3 % (6 cells) and index >= 97 (3 cells)
    }

    [Fact]
    public void Csv_rows_parse_with_missing_values_left_out()
    {
        const string csv = """
            cell_x,cell_y,lat,lon,ood,idx_cloudberry,p_cloudberry,sd_cloudberry,rule_cloudberry,lc_tree,ph,fi_site_main
            420125,7190125,64.826,25.317,0,78.5,0.0464,0.426,0.02,0.014,5.2,
            420375,7190125,64.826,25.322,1,99.1,0.31,0.2,0.5,0.9,,3
            """;
        var cells = GridCsv.Parse(new StringReader(csv), ["cloudberry"]);

        Assert.Equal(2, cells.Count);
        Assert.Equal((420125, 7190125, false), (cells[0].X, cells[0].Y, cells[0].Ood));
        Assert.Equal(78.5, cells[0].Scores["cloudberry"].Index);
        Assert.Equal(0.02, cells[0].Scores["cloudberry"].Rule);
        Assert.False(cells[0].Habitat.ContainsKey("fi_site_main"));
        Assert.True(cells[1].Ood);
        Assert.False(cells[1].Habitat.ContainsKey("ph"));
        Assert.Equal(3, cells[1].Habitat["fi_site_main"]);
    }

    [Fact]
    public void A_csv_without_a_species_column_fails_with_its_name()
    {
        var error = Assert.Throws<InvalidDataException>(() => GridCsv.Parse(new StringReader("cell_x,cell_y,ood\n1,2,0\n"), ["cloudberry"]));
        Assert.Contains("idx_cloudberry", error.Message);
    }

    [Fact]
    public void Cells_round_trip_through_their_database_json()
    {
        var cell = GridCell.FromJson(5, 6, false, """{"cloudberry": {"idx": 96.5, "p": 0.3, "sd": 0.25, "rule": null}}""", """{"lc_tree": 0.4, "ph": 4.6}""");
        Assert.Equal(96.5, cell.Scores["cloudberry"].Index);
        Assert.Null(cell.Scores["cloudberry"].Rule);
        Assert.Equal(0.4, cell.Habitat["lc_tree"]);
    }

    [Fact]
    public void The_shipped_model_card_matches_the_run_and_its_gate_decisions()
    {
        var card = ModelCard.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", "berry_model_card.json")));

        Assert.Equal("gbif", card.LabelMode);                 // trained on real sightings (the better of the two Kaggle runs)
        Assert.Equal(10, card.Species.Count);
        Assert.All(card.Species.Keys, s => Assert.True(BerryCatalog.TryGet(s, out _), s));
        Assert.All(card.Species.Values, m => Assert.Null(m.RulesAuc));

        // Every berry beat chance on held-out regions; the stand-data baselines stay in production for bilberry and lingonberry
        var decisions = card.Species.ToDictionary(s => s.Key, s => ModelGate.Decide(s.Value, Species.Supported.Contains(s.Key)).Status);
        Assert.Equal("candidate", decisions["bilberry"]);
        Assert.Equal("candidate", decisions["lingonberry"]);
        Assert.Equal(8, decisions.Count(d => d.Value == "production"));
    }

    [Fact]
    public void The_shipped_grid_parses_and_its_bog_berry_spots_are_on_mires()
    {
        var card = ModelCard.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", "berry_model_card.json")));
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", Path.GetFileName(card.GridFile))), CompressionMode.Decompress);
        var cells = GridCsv.Parse(new StreamReader(gz), card.Species.Keys.ToList());

        Assert.InRange(cells.Count, 5000, 10000);                                   // 25 km x 25 km at 250 m, water removed
        Assert.All(cells.Take(500), c => Assert.Equal(10, c.Scores.Count));

        var picked = ModelGate.Select(cells, "cloudberry");
        Assert.InRange(picked.Count, 1, cells.Count / 10);
        Assert.All(picked, p => Assert.True(p.Mire));
    }
}
