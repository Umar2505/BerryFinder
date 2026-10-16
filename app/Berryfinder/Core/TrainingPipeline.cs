using System.Text.Json.Serialization;

// The habitat-model training pipeline, free of any database or platform type so it runs the same in
// the app, in tests and from a script. The app's TrainingService builds the rows from accepted,
// consented field observations and stores what this returns in the model registry.
public static class TrainingPipeline
{
    public const int MinRows = 200;
    public const int MinPerClass = 30;
    public const int MinBlocks = 5;
    public const double MinAucGainOverBaseline = 0.02;
    public const double MaxCalibrationError = 0.10;
    public const int MinTestRows = 50;

    public sealed record Row(double[] Features, int Label, string Block, int Season, double BaselineScore, string Contributor);

    public sealed record ModelScore(string Kind, string Hyperparameters, double? CvAuc, double CvPrecisionAt20);

    public sealed record TestScore(string Kind, double? Auc, double Brier, double CalibrationError, double PrecisionAt10, double PrecisionAt20, int Rows,
        IReadOnlyList<Ml.Metrics.CalibrationBin> Reliability);

    public sealed record Report(
        string Status,                      // insufficient_data | candidate | rejected
        IReadOnlyList<string> Gaps,
        int Rows, int Positives, int Negatives, int Blocks, int Seasons, int Contributors,
        int RowsDroppedByContributorCap,
        string Split,
        IReadOnlyList<ModelScore> CrossValidation,
        string? Chosen,
        TestScore? Baseline,
        TestScore? Model,
        bool EligibleForPromotion,
        IReadOnlyList<string> Reasons)
    {
        [JsonIgnore] public Ml.IModel? Fitted { get; init; }
        [JsonIgnore] public Ml.Calibrator? Calibrator { get; init; }
    }

    public static Report Run(IReadOnlyList<Row> input, int seed = 11)
    {
        var rows = CapContributors(input, out var dropped, seed);
        var positives = rows.Count(r => r.Label == 1);
        var negatives = rows.Count - positives;
        var blocks = rows.Select(r => r.Block).Distinct().Count();
        var seasons = rows.Select(r => r.Season).Distinct().Count();
        var contributors = rows.Select(r => r.Contributor).Distinct().Count();

        var gaps = new List<string>();
        if (rows.Count < MinRows) gaps.Add($"{rows.Count} usable labelled observations; at least {MinRows} are needed");
        if (positives < MinPerClass) gaps.Add($"{positives} presence observations; at least {MinPerClass} are needed");
        if (negatives < MinPerClass) gaps.Add($"{negatives} properly surveyed absences; at least {MinPerClass} are needed");
        if (blocks < MinBlocks) gaps.Add($"Observations cover {blocks} spatial blocks (5 km tiles); at least {MinBlocks} are needed for spatial validation");

        if (gaps.Count > 0)
        {
            return new Report("insufficient_data", gaps, rows.Count, positives, negatives, blocks, seasons, contributors, dropped,
                "none", [], null, null, null, false, ["Training did not run: the data requirements above are not met"]);
        }

        // Hold out whole spatial blocks for the final test; with two or more seasons, also hold out the
        // latest season, so the test is both somewhere and somewhen the model has not seen
        var blockFolds = Ml.SpatialFolds(rows.Select(r => r.Block).ToList(), 5, seed);
        var testBlockRows = blockFolds[0].ToHashSet();
        var latestSeason = rows.Max(r => r.Season);
        var temporal = seasons >= 2;

        var test = rows.Where((r, i) => testBlockRows.Contains(i) || (temporal && r.Season == latestSeason)).ToList();
        var train = rows.Where((r, i) => !testBlockRows.Contains(i) && !(temporal && r.Season == latestSeason)).ToList();
        var split = temporal
            ? $"Spatial blocks (20% of 5 km tiles) plus the {latestSeason} season held out"
            : "Spatial blocks (20% of 5 km tiles) held out; only one season available, so no temporal hold-out";

        if (train.Count < MinRows / 2 || train.Select(r => r.Label).Distinct().Count() < 2 || test.Select(r => r.Label).Distinct().Count() < 2)
        {
            return new Report("insufficient_data", ["The held-out split left too few rows of one class to train and test"], rows.Count, positives, negatives,
                blocks, seasons, contributors, dropped, split, [], null, null, null, false, ["Training did not run"]);
        }

        var candidates = new List<(string Kind, string Hyper, Func<IReadOnlyList<Row>, Ml.IModel> Fit)>
        {
            ("logistic", "l2=0.1", r => Ml.LogisticRegression.Fit(r.Select(x => x.Features).ToList(), r.Select(x => x.Label).ToList(), 0.1)),
            ("logistic", "l2=1", r => Ml.LogisticRegression.Fit(r.Select(x => x.Features).ToList(), r.Select(x => x.Label).ToList(), 1.0)),
            ("logistic", "l2=10", r => Ml.LogisticRegression.Fit(r.Select(x => x.Features).ToList(), r.Select(x => x.Label).ToList(), 10.0)),
            ("random_forest", "depth=4", r => Ml.RandomForest.Fit(r.Select(x => x.Features).ToList(), r.Select(x => x.Label).ToList(), 150, 4, 5, seed)),
            ("random_forest", "depth=6", r => Ml.RandomForest.Fit(r.Select(x => x.Features).ToList(), r.Select(x => x.Label).ToList(), 150, 6, 5, seed)),
        };

        // Spatial cross-validation on the training part chooses the model family and its settings
        var folds = Ml.SpatialFolds(train.Select(r => r.Block).ToList(), 5, seed + 1);
        var cv = new List<ModelScore> { CrossValidate("baseline", "fixed", train, folds, null) };
        var oof = new Dictionary<string, double[]>();

        foreach (var (kind, hyper, fit) in candidates)
        {
            var score = CrossValidate(kind, hyper, train, folds, fit, oof);
            cv.Add(score);
        }

        var baselineCv = cv[0];
        var best = cv.Skip(1).Where(s => s.CvAuc is not null).OrderByDescending(s => s.CvAuc).FirstOrDefault();
        var reasons = new List<string>();

        if (best is null)
        {
            return new Report("rejected", [], rows.Count, positives, negatives, blocks, seasons, contributors, dropped, split, cv, null, null, null, false,
                ["No fold contained both classes, so no model could be scored"]);
        }

        var chosen = candidates.First(c => c.Kind == best.Kind && c.Hyper == best.Hyperparameters);
        var fitted = chosen.Fit(train);
        var calibrator = Ml.Calibrator.Fit(oof[$"{best.Kind}:{best.Hyperparameters}"], train.Select(r => r.Label).ToList());

        var testLabels = test.Select(r => r.Label).ToList();
        var modelProbabilities = test.Select(r => calibrator.Apply(fitted.PredictProbability(r.Features))).ToList();
        var baselineCalibrator = Ml.Calibrator.Fit(train.Select(r => r.BaselineScore).ToList(), train.Select(r => r.Label).ToList());
        var baselineProbabilities = test.Select(r => baselineCalibrator.Apply(r.BaselineScore)).ToList();

        var baselineTest = Evaluate("baseline", baselineProbabilities, testLabels);
        var modelTest = Evaluate($"{best.Kind} ({best.Hyperparameters})", modelProbabilities, testLabels);

        var eligible = true;
        if (test.Count < MinTestRows) { eligible = false; reasons.Add($"Only {test.Count} held-out rows; {MinTestRows} are needed to trust the test"); }
        if (modelTest.Auc is null || baselineTest.Auc is null || modelTest.Auc < baselineTest.Auc + MinAucGainOverBaseline)
        {
            eligible = false;
            reasons.Add($"Held-out AUC {modelTest.Auc:0.000} does not beat the ecological baseline's {baselineTest.Auc:0.000} by {MinAucGainOverBaseline}");
        }
        if (modelTest.CalibrationError > MaxCalibrationError) { eligible = false; reasons.Add($"Calibration error {modelTest.CalibrationError:0.000} is above {MaxCalibrationError}"); }
        if (modelTest.PrecisionAt20 < baselineTest.PrecisionAt20) { eligible = false; reasons.Add("Top-ranked sites are less often right than the baseline's"); }
        if (eligible) reasons.Add("Beats the ecological baseline on held-out blocks and is well calibrated");

        return new Report(eligible ? "candidate" : "rejected", [], rows.Count, positives, negatives, blocks, seasons, contributors, dropped, split, cv,
            $"{best.Kind} ({best.Hyperparameters})", baselineTest, modelTest, eligible, reasons)
        {
            Fitted = fitted,
            Calibrator = calibrator,
        };
    }

    private static ModelScore CrossValidate(string kind, string hyper, IReadOnlyList<Row> rows, int[][] folds, Func<IReadOnlyList<Row>, Ml.IModel>? fit,
        Dictionary<string, double[]>? outOfFold = null)
    {
        var predictions = new double[rows.Count];

        for (var f = 0; f < folds.Length; f++)
        {
            var held = folds[f].ToHashSet();
            if (held.Count == 0)
            {
                continue;
            }

            var trainPart = rows.Where((_, i) => !held.Contains(i)).ToList();
            Ml.IModel? model = fit is null || trainPart.Select(r => r.Label).Distinct().Count() < 2 ? null : fit(trainPart);

            foreach (var i in held)
            {
                predictions[i] = model?.PredictProbability(rows[i].Features) ?? rows[i].BaselineScore;
            }
        }

        outOfFold?.Add($"{kind}:{hyper}", predictions);
        var labels = rows.Select(r => r.Label).ToList();
        return new ModelScore(kind, hyper, Ml.Metrics.Auc(predictions, labels), Ml.Metrics.PrecisionAtTop(predictions, labels, 0.2));
    }

    private static TestScore Evaluate(string kind, IReadOnlyList<double> probabilities, IReadOnlyList<int> labels) => new(
        kind,
        Ml.Metrics.Auc(probabilities, labels),
        Ml.Metrics.Brier(probabilities, labels),
        Ml.Metrics.ExpectedCalibrationError(probabilities, labels),
        Ml.Metrics.PrecisionAtTop(probabilities, labels, 0.1),
        Ml.Metrics.PrecisionAtTop(probabilities, labels, 0.2),
        labels.Count,
        Ml.Metrics.Reliability(probabilities, labels));

    // Volunteer data is dominated by a few keen contributors; cap any one contributor's share so the
    // model learns the forests, not one person's favourite spots
    public static List<Row> CapContributors(IReadOnlyList<Row> rows, out int dropped, int seed)
    {
        var cap = Math.Max(25, (int)(rows.Count * 0.15));
        var random = new Random(seed);
        var kept = rows.GroupBy(r => r.Contributor)
            .SelectMany(g => g.Count() <= cap ? g : g.OrderBy(_ => random.Next()).Take(cap))
            .ToList();
        dropped = rows.Count - kept.Count;
        return kept;
    }

    // Feature vector for one stand. Missing values are imputed with a neutral value and flagged, so a
    // model can learn that "not recorded" differs from a recorded middle value.
    public static readonly string[] FeatureNames =
    [
        "fertility_class", "fertility_missing", "mineral_soil_site", "mire_site", "development_rank", "mean_age", "basal_area", "mean_height",
        "prop_pine", "prop_spruce", "prop_other", "peat_soil", "coarse_soil", "log_dist_road_m", "ndvi_summer", "ndvi_missing", "baseline_score",
    ];

    public static double[] Features(HabitatBaseline.StandInput s, double? distRoadM, double baselineScore) =>
    [
        s.Fertility ?? 4,
        s.Fertility is null ? 1 : 0,
        s.SiteMainClass == "1" ? 1 : 0,
        s.SiteMainClass is "2" or "3" or "4" or "5" ? 1 : 0,
        DevelopmentRank(s.Development, s.MeanAge),
        s.MeanAge ?? 50,
        s.BasalArea ?? 15,
        s.MeanHeight ?? 12,
        s.PropPine ?? 0.4,
        s.PropSpruce ?? 0.3,
        s.PropOther ?? 0.3,
        s.SoilClass is SoilClassifier.ThickPeat or SoilClassifier.ThinPeat ? 1 : 0,
        s.SoilClass == SoilClassifier.Coarse ? 1 : 0,
        Math.Log10(1 + (distRoadM ?? 500)),
        s.NdviSummer ?? 0.6,
        s.NdviSummer is null ? 1 : 0,
        baselineScore,
    ];

    public static double DevelopmentRank(string? development, double? meanAge) => development switch
    {
        "A0" => 0, "S0" => 0.5, "T1" => 1, "T2" => 2, "Y1" => 2, "02" => 3, "03" => 4, "04" => 5, "05" => 4.5, "ER" => 4,
        _ => meanAge is { } age ? Math.Clamp(age / 20.0, 0, 5) : 3,
    };

    // How a field observation becomes a label. Only presence or a deliberate, effortful search that
    // found nothing counts; an unsurveyed place is never an absence.
    public static int? Label(string method, string outcome, int? effortMinutes) => outcome switch
    {
        "few" or "moderate" or "many" or "unripe" => 1,
        "none" when method == "plot_1m2" => 0,
        "none" when method == "visit" && effortMinutes >= 10 => 0,
        _ => null,
    };
}
