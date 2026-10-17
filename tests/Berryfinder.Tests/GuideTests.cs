using Xunit;

public class GuideTests
{
    private static Candidate Area(long id, string species = "lingonberry", string suitability = "high", double ha = 8, double lat = 65.0, double lon = 25.5,
        string evidence = "good", double score = 0.9, double? road = 40) =>
        new(id, species, lat, lon, lat, lon, suitability, evidence, score, ha, road);

    [Fact]
    public void A_closer_place_beats_an_equally_good_farther_one()
    {
        var near = Guide.Utility(Area(1), 10, "peak");
        var far = Guide.Utility(Area(2), 50, "peak");
        Assert.True(near > far);
    }

    [Fact]
    public void A_much_better_place_can_justify_a_longer_drive()
    {
        var excellent = Guide.Utility(Area(1, "lingonberry", "high", ha: 20, score: 0.95), 35, "peak");
        var mediocre = Guide.Utility(Area(2, "lingonberry", "moderate", ha: 2, score: 0.5), 10, "peak");
        Assert.True(excellent > mediocre);
    }

    [Fact]
    public void A_berry_that_is_ripe_now_beats_one_that_is_nearly_over()
    {
        Assert.True(Guide.Utility(Area(1), 15, "peak") > Guide.Utility(Area(1), 15, "late"));
        Assert.True(Guide.Utility(Area(1), 15, "starting") > Guide.Utility(Area(1), 15, "late") * 0.9);
    }

    [Theory]
    [InlineData("not_yet")]
    [InlineData("over")]
    public void Nothing_is_recommended_outside_the_season(string availability)
    {
        Assert.Equal(0, Guide.Utility(Area(1), 5, availability));
        Assert.Empty(Guide.PickTop([new Ranked(Area(1), 5, availability, Guide.Utility(Area(1), 5, availability))], 3));
    }

    [Fact]
    public void Suggestions_are_different_outings_not_neighbouring_patches()
    {
        var best = Area(1, lat: 65.000, lon: 25.500);
        var neighbour = Area(2, lat: 65.002, lon: 25.502);           // a few hundred metres away
        var elsewhere = Area(3, lat: 65.100, lon: 25.700, suitability: "moderate");
        var options = new[] { best, neighbour, elsewhere }.Select(c => new Ranked(c, 10, "peak", Guide.Utility(c, 10, "peak"))).ToList();

        var picks = Guide.PickTop(options, 3);

        Assert.Equal([1, 3], picks.Select(p => p.Candidate.AreaId));
    }

    [Fact]
    public void PickTop_returns_best_first_and_respects_the_limit()
    {
        var options = Enumerable.Range(1, 10).Select(i => Area(i, lat: 65 + i * 0.05, ha: 3 + i)).Select(c => new Ranked(c, 10, "peak", Guide.Utility(c, 10, "peak"))).ToList();
        var picks = Guide.PickTop(options, 3);
        Assert.Equal(3, picks.Count);
        Assert.True(picks[0].Utility >= picks[1].Utility && picks[1].Utility >= picks[2].Utility);
    }

    [Fact]
    public void Another_berry_in_season_gets_a_place_among_the_suggestions()
    {
        var bilberries = Enumerable.Range(1, 5).Select(i => Area(i, "bilberry", lat: 65 + i * 0.05));
        var cloudberry = Area(9, "cloudberry", lat: 64.8, evidence: "limited", suitability: "moderate");
        var options = bilberries.Append(cloudberry).Select(c => new Ranked(c, 10, "peak", Guide.Utility(c, 10, "peak"))).ToList();

        var picks = Guide.PickTop(options, 3);

        Assert.Equal(3, picks.Count);
        Assert.Equal("bilberry", picks[0].Candidate.Species);                  // the best place still leads
        Assert.Equal(2, picks.Count(p => p.Candidate.Species == "bilberry"));
        Assert.Contains(picks, p => p.Candidate.Species == "cloudberry");
    }

    [Fact]
    public void One_berry_fills_every_place_when_nothing_else_is_in_season()
    {
        var options = Enumerable.Range(1, 5).Select(i => Area(i, "lingonberry", lat: 65 + i * 0.05))
            .Select(c => new Ranked(c, 10, "late", Guide.Utility(c, 10, "late"))).ToList();
        Assert.Equal(3, Guide.PickTop(options, 3).Count);
    }

    [Theory]
    [InlineData("high", "good", Odds.Promising)]
    [InlineData("high", "limited", Odds.WorthATry)]
    [InlineData("moderate", "fair", Odds.WorthATry)]
    [InlineData("moderate", "limited", Odds.LongShot)]
    [InlineData("low", "good", Odds.LongShot)]
    public void Odds_follow_suitability_and_never_exceed_it(string suitability, string evidence, Odds expected) =>
        Assert.Equal(expected, Guide.OddsOf(suitability, evidence));

    [Fact]
    public void Odds_are_worded_in_both_languages()
    {
        foreach (var odds in Enum.GetValues<Odds>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Guide.OddsLabel(odds, "en")));
            Assert.False(string.IsNullOrWhiteSpace(Guide.OddsLabel(odds, "fi")));
        }
    }

    [Theory]
    [InlineData(null, "Right by the road")]
    [InlineData(40.0, "Right by the road")]
    [InlineData(300.0, "7 min walk")]
    public void Walking_is_an_allowance_in_plain_words(double? metres, string expected) => Assert.Equal(expected, Guide.WalkText(metres, "en"));

    [Fact]
    public void An_unknown_drive_time_says_so_instead_of_inventing_one()
    {
        Assert.Equal("Drive time unavailable", Guide.DriveText(null, "en"));
        Assert.Equal("14 min drive", Guide.DriveText(14.2, "en"));
        Assert.Equal("14 min ajomatka", Guide.DriveText(14.2, "fi"));
    }

    [Fact]
    public void Habitat_is_described_in_plain_words()
    {
        var pine = Guide.HabitatOf(pine: 0.8, spruce: 0.1, broadleaf: 0.1, meanAge: 85, fertility: 4);
        Assert.Equal("Mature pine forest · dry ground", Guide.Describe(pine, "en"));
        Assert.Equal("Varttunut mäntymetsä · kuiva maa", Guide.Describe(pine, "fi"));

        var mixed = Guide.HabitatOf(0.4, 0.4, 0.2, null, null);
        Assert.Equal("Mixed forest", Guide.Describe(mixed, "en"));
    }

    [Fact]
    public void Haversine_matches_a_known_distance()
    {
        // Oulu to Helsinki is about 535 km as the crow flies
        Assert.InRange(Guide.HaversineKm(65.01, 25.47, 60.17, 24.94), 525, 545);
    }
}

public class BerryCatalogTests
{
    [Fact]
    public void Every_berry_has_art_names_and_looks_in_both_languages()
    {
        Assert.Equal(10, BerryCatalog.All.Count);
        foreach (var b in BerryCatalog.All)
        {
            Assert.Matches("^#[0-9A-F]{6}$", b.Color);
            Assert.Equal($"/berries/{b.Id}.svg", b.Art);
            foreach (var lang in new[] { "en", "fi" })
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Name(lang)));
                Assert.False(string.IsNullOrWhiteSpace(b.Look(lang)));
                Assert.False(string.IsNullOrWhiteSpace(b.Where(lang)));
                Assert.False(string.IsNullOrWhiteSpace(b.Tip(lang)));
            }
        }

        Assert.Equal(BerryCatalog.All.Count, BerryCatalog.All.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void Every_berry_with_a_stand_baseline_is_in_the_catalogue()
    {
        Assert.All(Species.Supported, s => Assert.True(BerryCatalog.TryGet(s, out _), s));
    }

    [Fact]
    public void Weaker_evidence_ranks_below_the_same_score_on_better_maps()
    {
        static Candidate With(string evidence) => new(1, "cloudberry", 65.0, 25.5, 65.0, 25.5, "high", evidence, 0.95, 8, 40);
        Assert.True(Guide.Utility(With("fair"), 15, "peak") > Guide.Utility(With("limited"), 15, "peak"));
    }

    [Fact]
    public void Grid_habitats_read_as_plain_words()
    {
        var bog = Guide.HabitatFromGrid(new Dictionary<string, double> { ["mire"] = 0.9, ["lc_tree"] = 0.1, ["soc"] = 220 });
        Assert.Equal("Open bog · peaty ground", Guide.Describe(bog, "en"));
        Assert.Equal("Avosuo · turvemaa", Guide.Describe(bog, "fi"));

        var pineBog = Guide.HabitatFromGrid(new Dictionary<string, double> { ["mire"] = 1, ["lc_tree"] = 0.8, ["soc"] = 100 });
        Assert.Equal("Wooded bog", Guide.Describe(pineBog, "en"));

        Assert.Equal("Open ground", Guide.Describe(Guide.HabitatFromGrid(new Dictionary<string, double> { ["lc_tree"] = 0.1 }), "en"));
        Assert.Equal("Forest edge or clearing", Guide.Describe(Guide.HabitatFromGrid(new Dictionary<string, double> { ["lc_tree"] = 0.45 }), "en"));

        var forest = Guide.HabitatFromGrid(new Dictionary<string, double> { ["lc_tree"] = 0.95, ["fi_prop_pine"] = 0.8, ["fi_prop_spruce"] = 0.1, ["fi_prop_other"] = 0.1, ["fi_age"] = 90, ["fi_fertility"] = 4 });
        Assert.Equal("Mature pine forest · dry ground", Guide.Describe(forest, "en"));
    }

    [Fact]
    public void Season_windows_are_valid_dates()
    {
        foreach (var b in BerryCatalog.All)
        {
            _ = new DateOnly(2026, b.SeasonStart.Month, b.SeasonStart.Day);
            _ = new DateOnly(2026, b.SeasonEnd.Month, b.SeasonEnd.Day);
            Assert.True(b.SeasonStart.Month * 100 + b.SeasonStart.Day < b.SeasonEnd.Month * 100 + b.SeasonEnd.Day, b.Id);
        }
    }

    [Theory]
    [InlineData("bilberry", 2026, 7, 20, true)]
    [InlineData("bilberry", 2026, 10, 17, false)]
    [InlineData("lingonberry", 2026, 10, 17, true)]
    [InlineData("lingonberry", 2026, 7, 1, false)]
    [InlineData("cranberry", 2026, 9, 15, true)]
    public void Season_membership_by_date(string id, int y, int m, int d, bool expected) =>
        Assert.Equal(expected, BerryCatalog.InSeason(BerryCatalog.Get(id), new DateOnly(y, m, d)));

    [Fact]
    public void Days_until_the_next_season_wrap_into_next_year()
    {
        var bilberry = BerryCatalog.Get("bilberry");
        Assert.Equal(0, BerryCatalog.DaysUntilStart(bilberry, new DateOnly(2026, 7, 20)));
        Assert.Equal(new DateOnly(2027, 7, 15).DayNumber - new DateOnly(2026, 10, 17).DayNumber, BerryCatalog.DaysUntilStart(bilberry, new DateOnly(2026, 10, 17)));
    }

    [Fact]
    public void Season_text_reads_naturally()
    {
        Assert.Equal("Mid July to late August", BerryCatalog.SeasonText(BerryCatalog.Get("bilberry"), "en"));
        Assert.Equal("Heinäkuun puoliväli – elokuun loppu", BerryCatalog.SeasonText(BerryCatalog.Get("bilberry"), "fi"));
    }
}
