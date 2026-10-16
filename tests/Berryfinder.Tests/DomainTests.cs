using Xunit;

public class HabitatBaselineTests
{
    private static readonly DateOnly Today = new(2026, 9, 1);

    private static HabitatBaseline.StandInput Stand(int fertility = 3, string development = "03", string main = "1", double basal = 18, string? soil = SoilClassifier.Till,
        double pine = 0.3, double spruce = 0.6, double other = 0.1, string? restriction = null, DateOnly? measured = null) =>
        new("1", main, fertility, 10, "1", development, 2, pine, spruce, other, 70, basal, 16, measured ?? new DateOnly(2024, 6, 1), "4", soil, null, restriction);

    [Fact]
    public void Bilberry_prefers_mesic_heath_over_barren_heath()
    {
        var mesic = HabitatBaseline.Score(Species.Bilberry, Stand(fertility: 3), Today);
        var barren = HabitatBaseline.Score(Species.Bilberry, Stand(fertility: 6), Today);
        Assert.True(mesic.Score > barren.Score);
        Assert.Equal("high", mesic.Suitability);
    }

    [Fact]
    public void Lingonberry_prefers_sub_xeric_pine_heath_over_herb_rich_spruce()
    {
        var dry = HabitatBaseline.Score(Species.Lingonberry, Stand(fertility: 4, pine: 0.8, spruce: 0.1, other: 0.1, soil: SoilClassifier.Coarse), Today);
        var rich = HabitatBaseline.Score(Species.Lingonberry, Stand(fertility: 2, pine: 0.05, spruce: 0.85, other: 0.1), Today);
        Assert.True(dry.Score > rich.Score);
    }

    [Theory]
    [InlineData(Species.Bilberry)]
    [InlineData(Species.Lingonberry)]
    public void A_recent_clear_cut_is_never_high_suitability(string species)
    {
        var cut = HabitatBaseline.Score(species, Stand(development: "A0"), Today);
        Assert.NotEqual("high", cut.Suitability);
    }

    [Fact]
    public void An_open_bog_is_unlikely_bilberry_habitat()
    {
        var bog = HabitatBaseline.Score(Species.Bilberry, Stand(main: "4", fertility: 6, soil: SoilClassifier.ThickPeat), Today);
        Assert.Contains(bog.Suitability, new[] { "unlikely", "low" });
    }

    [Fact]
    public void A_stand_with_no_site_fertility_is_not_assessed_instead_of_guessed()
    {
        var input = new HabitatBaseline.StandInput("1", "1", null, null, null, "03", null, null, null, null, null, null, null, null, null, null, null, null);
        var result = HabitatBaseline.Score(Species.Bilberry, input, Today);
        Assert.Equal("not_assessed", result.Suitability);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Missing_soil_lowers_evidence_quality_but_not_the_class()
    {
        var withSoil = HabitatBaseline.Score(Species.Bilberry, Stand(soil: SoilClassifier.Till), Today);
        var noSoil = HabitatBaseline.Score(Species.Bilberry, Stand(soil: null), Today);
        Assert.Equal(withSoil.Suitability, noSoil.Suitability);
        Assert.True(Rank(noSoil.Evidence) <= Rank(withSoil.Evidence));
    }

    [Fact]
    public void Old_measurements_reduce_evidence_quality()
    {
        var fresh = HabitatBaseline.Score(Species.Bilberry, Stand(measured: new DateOnly(2025, 6, 1)), Today);
        var stale = HabitatBaseline.Score(Species.Bilberry, Stand(measured: new DateOnly(2008, 6, 1)), Today);
        Assert.True(Rank(stale.Evidence) < Rank(fresh.Evidence));
    }

    [Fact]
    public void A_strict_restriction_flags_the_stand_as_excluded()
    {
        Assert.True(HabitatBaseline.Score(Species.Bilberry, Stand(restriction: "exclude"), Today).Excluded);
        Assert.False(HabitatBaseline.Score(Species.Bilberry, Stand(restriction: "warn"), Today).Excluded);
    }

    [Fact]
    public void Every_result_explains_itself()
    {
        var result = HabitatBaseline.Score(Species.Bilberry, Stand(), Today);
        Assert.NotEmpty(result.Drivers);
        Assert.All(result.Drivers, d => { Assert.False(string.IsNullOrWhiteSpace(d.En)); Assert.False(string.IsNullOrWhiteSpace(d.Fi)); });
    }

    [Fact]
    public void An_unsupported_species_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HabitatBaseline.Score(Species.Cloudberry, Stand(), Today));
    }

    private static int Rank(string evidence) => evidence switch { "good" => 3, "fair" => 2, _ => 1 };
}

public class SoilClassifierTests
{
    [Theory]
    [InlineData("Paksu turvekerros (Tvp) RT", SoilClassifier.ThickPeat)]
    [InlineData("Ohut turvekerros (Tvo) RT", SoilClassifier.ThinPeat)]
    [InlineData("Soistuma (Tvs) RT", SoilClassifier.ThinPeat)]
    [InlineData("Sekalajitteinen maalaji, päälajitetta ei selvitetty (SY) RT", SoilClassifier.Till)]
    [InlineData("Karkearakeinen maalaji, päälajitetta ei selvitetty (KY) RT", SoilClassifier.Coarse)]
    [InlineData("Hienojakoinen maalaji, päälajitetta ei selvitetty (HY) RT", SoilClassifier.Fine)]
    [InlineData("Kartoittamaton (0)", SoilClassifier.Unmapped)]
    [InlineData(null, SoilClassifier.Unmapped)]
    public void Real_GTK_labels_map_to_habitat_classes(string? label, string expected) => Assert.Equal(expected, SoilClassifier.Classify(label));
}

public class PhenologyTests
{
    private static List<Phenology.DailyWeather> Season(int year, double meanTemp, DateOnly last, double minTemp = 3)
    {
        var days = new List<Phenology.DailyWeather>();
        for (var d = new DateOnly(year, 3, 25); d <= last; d = d.AddDays(1))
        {
            days.Add(new Phenology.DailyWeather(d, meanTemp, minTemp, 2, 0));
        }

        return days;
    }

    [Fact]
    public void Winter_is_dormant_without_needing_weather()
    {
        var result = Phenology.Estimate(Species.Bilberry, new DateOnly(2026, 2, 1), [], null);
        Assert.Equal("dormant", result.Stage);
        Assert.Equal("over", result.Availability);
    }

    [Fact]
    public void Missing_weather_in_season_is_unknown_not_invented()
    {
        var result = Phenology.Estimate(Species.Bilberry, new DateOnly(2026, 8, 1), [], null);
        Assert.Equal("unknown", result.Stage);
        Assert.Equal("none", result.Source);
    }

    [Fact]
    public void A_warm_season_reaches_ripe_by_August_and_a_cold_one_does_not()
    {
        var date = new DateOnly(2026, 8, 5);
        var warm = Phenology.Estimate(Species.Bilberry, date, Season(2026, 15, date), null);
        var cold = Phenology.Estimate(Species.Bilberry, date, Season(2026, 8.5, date), null);

        Assert.Equal("station", warm.Source);
        Assert.True(warm.Gdd > cold.Gdd);
        Assert.Contains(warm.Availability, new[] { "peak", "late", "starting" });
        Assert.Equal("not_yet", cold.Availability);
    }

    [Fact]
    public void Frost_during_flowering_is_reported()
    {
        var date = new DateOnly(2026, 8, 1);
        // A cool spring (7 °C mean, about 2 growing degrees a day) puts bilberry flowering in late April to early July
        var days = Season(2026, 7, date);
        var flowerDay = days.FindIndex(d => d.Day == new DateOnly(2026, 5, 25));
        days[flowerDay] = days[flowerDay] with { TMin = -4 };

        var result = Phenology.Estimate(Species.Bilberry, date, days, null);
        Assert.Contains(result.Notes, n => n.Key == "frost");
    }

    [Fact]
    public void Gaps_in_the_station_fall_back_to_the_labelled_model_grid()
    {
        var date = new DateOnly(2026, 8, 5);
        var gridDays = Season(2026, 14, date).Select(d => d with { RootWetness = 0.6 }).ToList();

        var result = Phenology.Estimate(Species.Bilberry, date, [], null, gridDays);

        Assert.Equal("nasa_power", result.Source);
        Assert.Contains(result.Notes, n => n.Key == "grid");
        Assert.Contains("NASA POWER", result.Basis);
        Assert.Equal(0.6, result.RootWetness);
    }

    [Fact]
    public void A_good_station_is_preferred_over_the_grid()
    {
        var date = new DateOnly(2026, 8, 5);
        var result = Phenology.Estimate(Species.Bilberry, date, Season(2026, 14, date), null, Season(2026, 20, date));
        Assert.Equal("station", result.Source);
    }
}

public class MlTests
{
    [Fact]
    public void Auc_is_one_for_perfect_ranking_half_for_ties_and_zero_for_reversed()
    {
        Assert.Equal(1.0, Ml.Metrics.Auc([0.1, 0.2, 0.8, 0.9], [0, 0, 1, 1]));
        Assert.Equal(0.0, Ml.Metrics.Auc([0.9, 0.8, 0.2, 0.1], [0, 0, 1, 1]));
        Assert.Equal(0.5, Ml.Metrics.Auc([0.5, 0.5, 0.5, 0.5], [0, 1, 0, 1]));
        Assert.Null(Ml.Metrics.Auc([0.1, 0.2], [1, 1]));
    }

    [Fact]
    public void Brier_score_of_a_perfect_forecast_is_zero()
    {
        Assert.Equal(0.0, Ml.Metrics.Brier([1, 0, 1], [1, 0, 1]), 12);
        Assert.Equal(0.25, Ml.Metrics.Brier([0.5, 0.5], [1, 0]), 12);
    }

    [Fact]
    public void Spatial_folds_keep_every_block_in_exactly_one_fold()
    {
        var blocks = Enumerable.Range(0, 200).Select(i => $"E{i % 20}").ToList();
        var folds = Ml.SpatialFolds(blocks, 5);

        Assert.Equal(200, folds.Sum(f => f.Length));
        foreach (var block in blocks.Distinct())
        {
            var inFolds = folds.Count(f => f.Any(i => blocks[i] == block));
            Assert.Equal(1, inFolds);
        }
    }

    // SYNTHETIC: generated from a known rule, to prove the learner can recover a signal. Never real data.
    private static (List<double[]> X, List<int> Y) SyntheticData(int n, int seed)
    {
        var random = new Random(seed);
        var xs = new List<double[]>();
        var ys = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var signal = random.NextDouble() * 2 - 1;
            var noise = random.NextDouble() * 2 - 1;
            var p = Ml.Sigmoid(3 * signal);
            xs.Add([signal, noise]);
            ys.Add(random.NextDouble() < p ? 1 : 0);
        }

        return (xs, ys);
    }

    [Fact]
    public void Logistic_regression_recovers_the_synthetic_signal_and_ignores_noise()
    {
        var (x, y) = SyntheticData(1500, 1);
        var model = Ml.LogisticRegression.Fit(x, y, 1.0);
        var (tx, ty) = SyntheticData(1500, 2);
        var auc = Ml.Metrics.Auc(tx.Select(model.PredictProbability).ToList(), ty);

        Assert.True(auc > 0.8, $"AUC {auc}");
        Assert.True(Math.Abs(model.Weights[0]) > 5 * Math.Abs(model.Weights[1]), "signal weight should dominate noise weight");
    }

    [Fact]
    public void Random_forest_learns_a_nonlinear_synthetic_rule()
    {
        var random = new Random(3);
        var xs = new List<double[]>();
        var ys = new List<int>();
        for (var i = 0; i < 1200; i++)
        {
            double a = random.NextDouble(), b = random.NextDouble();
            xs.Add([a, b]);
            ys.Add((a > 0.5) ^ (b > 0.5) ? 1 : 0);   // XOR: a linear model cannot learn this
        }

        var forest = Ml.RandomForest.Fit(xs, ys, 60, 5, 5);
        var linear = Ml.LogisticRegression.Fit(xs, ys, 1.0);
        var forestAuc = Ml.Metrics.Auc(xs.Select(forest.PredictProbability).ToList(), ys)!.Value;
        var linearAuc = Ml.Metrics.Auc(xs.Select(linear.PredictProbability).ToList(), ys)!.Value;

        Assert.True(forestAuc > 0.9, $"forest AUC {forestAuc}");
        Assert.True(linearAuc < 0.65, $"linear AUC {linearAuc}");
    }

    [Fact]
    public void Platt_calibration_pulls_overconfident_scores_toward_observed_rates()
    {
        var random = new Random(5);
        var scores = new List<double>();
        var labels = new List<int>();
        for (var i = 0; i < 3000; i++)
        {
            var truth = random.NextDouble();
            scores.Add(Math.Clamp(truth * 1.8 - 0.4, 0, 1));   // distorted but monotone
            labels.Add(random.NextDouble() < truth ? 1 : 0);
        }

        var calibrator = Ml.Calibrator.Fit(scores, labels);
        var before = Ml.Metrics.ExpectedCalibrationError(scores, labels);
        var after = Ml.Metrics.ExpectedCalibrationError(scores.Select(calibrator.Apply).ToList(), labels);
        Assert.True(after < before, $"ECE before {before:0.000}, after {after:0.000}");
    }
}

public class TrainingPipelineTests
{
    private static TrainingPipeline.Row Row(Random random, string block, int season, string contributor, bool informative)
    {
        var fertility = random.Next(1, 7);
        var y = informative ? (fertility is 3 or 4 ? (random.NextDouble() < 0.8 ? 1 : 0) : (random.NextDouble() < 0.15 ? 1 : 0)) : random.Next(2);
        var baseline = fertility is 3 or 4 ? 0.7 : 0.3;
        return new TrainingPipeline.Row([fertility, random.NextDouble(), baseline], y, block, season, baseline, contributor);
    }

    [Fact]
    public void Too_little_data_refuses_to_train_and_says_what_is_missing()
    {
        var random = new Random(1);
        var rows = Enumerable.Range(0, 40).Select(i => Row(random, $"B{i % 3}", 2026, $"u{i}", true)).ToList();

        var report = TrainingPipeline.Run(rows);

        Assert.Equal("insufficient_data", report.Status);
        Assert.False(report.EligibleForPromotion);
        Assert.Null(report.Fitted);
        Assert.Contains(report.Gaps, g => g.Contains("200"));
        Assert.Contains(report.Gaps, g => g.Contains("spatial blocks"));
    }

    [Fact]
    public void Sufficient_synthetic_data_trains_with_a_spatial_split_and_reports_baseline_comparison()
    {
        var random = new Random(2);
        var rows = Enumerable.Range(0, 900).Select(i => Row(random, $"B{i % 25}", 2025 + i % 2, $"u{i % 40}", true)).ToList();

        var report = TrainingPipeline.Run(rows);

        Assert.NotEqual("insufficient_data", report.Status);
        Assert.NotNull(report.Model);
        Assert.NotNull(report.Baseline);
        Assert.Contains("season", report.Split);
        Assert.True(report.CrossValidation.Count >= 6);
        Assert.Equal("baseline", report.CrossValidation[0].Kind);
    }

    [Fact]
    public void Pure_noise_labels_never_become_promotable()
    {
        var random = new Random(3);
        var rows = Enumerable.Range(0, 900).Select(i => Row(random, $"B{i % 25}", 2025 + i % 2, $"u{i % 40}", false)).ToList();
        Assert.False(TrainingPipeline.Run(rows).EligibleForPromotion);
    }

    [Fact]
    public void One_keen_contributor_cannot_dominate_the_training_set()
    {
        var random = new Random(4);
        var rows = Enumerable.Range(0, 500).Select(i => Row(random, $"B{i % 25}", 2026, "keen", true))
            .Concat(Enumerable.Range(0, 500).Select(i => Row(random, $"B{i % 25}", 2026, $"u{i % 50}", true))).ToList();

        var capped = TrainingPipeline.CapContributors(rows, out var dropped, 1);

        Assert.True(capped.Count(r => r.Contributor == "keen") <= Math.Max(25, (int)(rows.Count * 0.15)));
        Assert.True(dropped > 0);
    }

    [Theory]
    [InlineData("few", "visit", 5, 1)]
    [InlineData("many", "visit", null, 1)]
    [InlineData("unripe", "visit", null, 1)]
    [InlineData("none", "plot_1m2", null, 0)]
    [InlineData("none", "visit", 30, 0)]
    public void Presence_and_properly_surveyed_absence_become_labels(string outcome, string method, int? minutes, int expected) =>
        Assert.Equal(expected, TrainingPipeline.Label(method, outcome, minutes));

    [Theory]
    [InlineData("not_surveyed", "visit", 60)]
    [InlineData("none", "visit", 3)]
    [InlineData("none", "visit", null)]
    public void Unsurveyed_places_and_cursory_visits_are_never_absences(string outcome, string method, int? minutes) =>
        Assert.Null(TrainingPipeline.Label(method, outcome, minutes));
}

public class PlansTests
{
    [Fact]
    public void Paid_plans_unlock_more_than_free()
    {
        Assert.True(Plans.Premium.RecommendationLimit > Plans.Free.RecommendationLimit);
        Assert.True(Plans.Premium.TripPlanner && !Plans.Free.TripPlanner);
        Assert.True(Plans.Professional.Export && !Plans.Premium.Export);
    }

    [Fact]
    public void Every_offer_belongs_to_a_known_plan_with_a_positive_price()
    {
        Assert.All(Plans.Offers, o =>
        {
            Assert.True(o.AmountMinor > 0);
            Assert.NotEqual(Plans.Free, Plans.For(o.PlanId));
        });
    }
}
