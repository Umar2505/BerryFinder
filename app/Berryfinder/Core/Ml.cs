// Small, dependency-free learners and metrics for the habitat model. The data sets this app will
// see in its first seasons are hundreds to a few thousand labelled plots, where CPU tree ensembles
// and a regularised linear model are the right tools; nothing here needs a GPU.
public static class Ml
{
    public interface IModel
    {
        double PredictProbability(double[] x);
    }

    // L2-regularised logistic regression on standardised features, fitted with Newton steps
    public sealed class LogisticRegression : IModel
    {
        public double[] Means { get; init; } = [];
        public double[] Scales { get; init; } = [];
        public double[] Weights { get; init; } = [];
        public double Bias { get; init; }

        public static LogisticRegression Fit(IReadOnlyList<double[]> xs, IReadOnlyList<int> ys, double l2 = 1.0, int iterations = 50)
        {
            var d = xs[0].Length;
            var means = new double[d];
            var scales = new double[d];

            for (var j = 0; j < d; j++)
            {
                means[j] = xs.Average(x => x[j]);
                var variance = xs.Average(x => (x[j] - means[j]) * (x[j] - means[j]));
                scales[j] = variance > 1e-12 ? Math.Sqrt(variance) : 1.0;
            }

            var z = xs.Select(x => Standardise(x, means, scales)).ToList();
            var beta = new double[d + 1];   // beta[0] is the bias

            for (var iter = 0; iter < iterations; iter++)
            {
                var gradient = new double[d + 1];
                var hessian = new double[d + 1, d + 1];

                for (var i = 0; i < z.Count; i++)
                {
                    var row = Augment(z[i]);
                    var p = Sigmoid(Dot(beta, row));
                    var weight = p * (1 - p);

                    for (var a = 0; a <= d; a++)
                    {
                        gradient[a] += (p - ys[i]) * row[a];
                        for (var b = 0; b <= d; b++)
                        {
                            hessian[a, b] += weight * row[a] * row[b];
                        }
                    }
                }

                // The penalty leaves the bias free
                for (var a = 1; a <= d; a++)
                {
                    gradient[a] += l2 * beta[a];
                    hessian[a, a] += l2;
                }

                hessian[0, 0] += 1e-9;
                var step = SolveLinearSystem(hessian, gradient);
                var change = 0.0;

                for (var a = 0; a <= d; a++)
                {
                    beta[a] -= step[a];
                    change = Math.Max(change, Math.Abs(step[a]));
                }

                if (change < 1e-7)
                {
                    break;
                }
            }

            return new LogisticRegression { Means = means, Scales = scales, Bias = beta[0], Weights = beta[1..] };
        }

        public double PredictProbability(double[] x) => Sigmoid(Bias + Dot(Weights, Standardise(x, Means, Scales)));

        private static double[] Augment(double[] x)
        {
            var row = new double[x.Length + 1];
            row[0] = 1;
            Array.Copy(x, 0, row, 1, x.Length);
            return row;
        }
    }

    // A small random forest of CART trees (Gini impurity, bootstrap rows, random feature subsets)
    public sealed class RandomForest : IModel
    {
        public List<Node> Trees { get; init; } = [];

        public sealed class Node
        {
            public int Feature { get; set; } = -1;
            public double Threshold { get; set; }
            public double Value { get; set; }
            public Node? Left { get; set; }
            public Node? Right { get; set; }
        }

        public static RandomForest Fit(IReadOnlyList<double[]> xs, IReadOnlyList<int> ys, int trees = 150, int maxDepth = 6, int minLeaf = 5, int seed = 17)
        {
            var random = new Random(seed);
            var d = xs[0].Length;
            var featuresPerSplit = Math.Max(1, (int)Math.Round(Math.Sqrt(d)));
            var forest = new List<Node>(trees);

            for (var t = 0; t < trees; t++)
            {
                var sample = Enumerable.Range(0, xs.Count).Select(_ => random.Next(xs.Count)).ToArray();
                forest.Add(Grow(xs, ys, sample, 0, maxDepth, minLeaf, featuresPerSplit, random));
            }

            return new RandomForest { Trees = forest };
        }

        public double PredictProbability(double[] x) => Trees.Average(t => Walk(t, x));

        private static double Walk(Node node, double[] x)
        {
            while (node.Feature >= 0)
            {
                node = x[node.Feature] <= node.Threshold ? node.Left! : node.Right!;
            }

            return node.Value;
        }

        private static Node Grow(IReadOnlyList<double[]> xs, IReadOnlyList<int> ys, int[] rows, int depth, int maxDepth, int minLeaf, int featuresPerSplit, Random random)
        {
            var positives = rows.Count(r => ys[r] == 1);
            var leaf = new Node { Value = (positives + 0.5) / (rows.Length + 1.0) };

            if (depth >= maxDepth || rows.Length < 2 * minLeaf || positives == 0 || positives == rows.Length)
            {
                return leaf;
            }

            var d = xs[0].Length;
            var candidates = Enumerable.Range(0, d).OrderBy(_ => random.Next()).Take(featuresPerSplit);
            var bestGain = 0.0;
            var bestFeature = -1;
            var bestThreshold = 0.0;
            var parent = Gini(positives, rows.Length);

            foreach (var feature in candidates)
            {
                var sorted = rows.OrderBy(r => xs[r][feature]).ToArray();
                var leftPositives = 0;

                for (var i = 0; i < sorted.Length - 1; i++)
                {
                    leftPositives += ys[sorted[i]];
                    var leftCount = i + 1;
                    var rightCount = sorted.Length - leftCount;

                    if (leftCount < minLeaf || rightCount < minLeaf || xs[sorted[i]][feature] == xs[sorted[i + 1]][feature])
                    {
                        continue;
                    }

                    var weighted = (leftCount * Gini(leftPositives, leftCount) + rightCount * Gini(positives - leftPositives, rightCount)) / sorted.Length;
                    var gain = parent - weighted;

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestFeature = feature;
                        bestThreshold = (xs[sorted[i]][feature] + xs[sorted[i + 1]][feature]) / 2;
                    }
                }
            }

            if (bestFeature < 0)
            {
                return leaf;
            }

            var left = rows.Where(r => xs[r][bestFeature] <= bestThreshold).ToArray();
            var right = rows.Where(r => xs[r][bestFeature] > bestThreshold).ToArray();

            return new Node
            {
                Feature = bestFeature,
                Threshold = bestThreshold,
                Left = Grow(xs, ys, left, depth + 1, maxDepth, minLeaf, featuresPerSplit, random),
                Right = Grow(xs, ys, right, depth + 1, maxDepth, minLeaf, featuresPerSplit, random),
            };
        }

        private static double Gini(int positives, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var p = (double)positives / count;
            return 2 * p * (1 - p);
        }
    }

    // Platt scaling: maps raw scores to probabilities with a one-feature logistic fit
    public sealed record Calibrator(double A, double B)
    {
        public static Calibrator Fit(IReadOnlyList<double> scores, IReadOnlyList<int> ys)
        {
            var model = LogisticRegression.Fit(scores.Select(s => new[] { s }).ToList(), ys, l2: 1e-3);
            return new Calibrator(model.Weights[0] / model.Scales[0], model.Bias - model.Weights[0] * model.Means[0] / model.Scales[0]);
        }

        public double Apply(double score) => Sigmoid(A * score + B);
    }

    public static double Sigmoid(double v) => v >= 0 ? 1 / (1 + Math.Exp(-v)) : Math.Exp(v) / (1 + Math.Exp(v));

    public static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static double[] Standardise(double[] x, double[] means, double[] scales)
    {
        var z = new double[x.Length];
        for (var j = 0; j < x.Length; j++)
        {
            z[j] = (x[j] - means[j]) / scales[j];
        }

        return z;
    }

    // Gaussian elimination with partial pivoting; the systems here are at most ~20 x 20
    public static double[] SolveLinearSystem(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = row;
                }
            }

            if (pivot != col)
            {
                for (var k = 0; k < n; k++)
                {
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                }

                (v[col], v[pivot]) = (v[pivot], v[col]);
            }

            var diagonal = Math.Abs(m[col, col]) < 1e-12 ? 1e-12 : m[col, col];

            for (var row = col + 1; row < n; row++)
            {
                var factor = m[row, col] / diagonal;
                for (var k = col; k < n; k++)
                {
                    m[row, k] -= factor * m[col, k];
                }

                v[row] -= factor * v[col];
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = v[row];
            for (var k = row + 1; k < n; k++)
            {
                sum -= m[row, k] * x[k];
            }

            x[row] = sum / (Math.Abs(m[row, row]) < 1e-12 ? 1e-12 : m[row, row]);
        }

        return x;
    }

    public static class Metrics
    {
        // Rank-based ROC AUC (Mann–Whitney), with ties counted as half
        public static double? Auc(IReadOnlyList<double> scores, IReadOnlyList<int> ys)
        {
            var positives = ys.Count(y => y == 1);
            var negatives = ys.Count - positives;

            if (positives == 0 || negatives == 0)
            {
                return null;
            }

            var order = Enumerable.Range(0, scores.Count).OrderBy(i => scores[i]).ToArray();
            var ranks = new double[scores.Count];
            var i0 = 0;

            while (i0 < order.Length)
            {
                var i1 = i0;
                while (i1 + 1 < order.Length && scores[order[i1 + 1]] == scores[order[i0]])
                {
                    i1++;
                }

                var rank = (i0 + i1) / 2.0 + 1;
                for (var k = i0; k <= i1; k++)
                {
                    ranks[order[k]] = rank;
                }

                i0 = i1 + 1;
            }

            var positiveRankSum = Enumerable.Range(0, ys.Count).Where(i => ys[i] == 1).Sum(i => ranks[i]);
            return (positiveRankSum - positives * (positives + 1) / 2.0) / ((double)positives * negatives);
        }

        public static double Brier(IReadOnlyList<double> probabilities, IReadOnlyList<int> ys) =>
            probabilities.Select((p, i) => (p - ys[i]) * (p - ys[i])).Average();

        public static double LogLoss(IReadOnlyList<double> probabilities, IReadOnlyList<int> ys) =>
            -probabilities.Select((p, i) => ys[i] == 1 ? Math.Log(Math.Clamp(p, 1e-9, 1)) : Math.Log(Math.Clamp(1 - p, 1e-9, 1))).Average();

        // Share of true positives among the top fraction of sites ranked by score
        public static double PrecisionAtTop(IReadOnlyList<double> scores, IReadOnlyList<int> ys, double fraction)
        {
            var k = Math.Max(1, (int)Math.Round(scores.Count * fraction));
            return Enumerable.Range(0, scores.Count).OrderByDescending(i => scores[i]).Take(k).Average(i => (double)ys[i]);
        }

        public sealed record CalibrationBin(double Lower, double Upper, int Count, double MeanPredicted, double Observed);

        public static IReadOnlyList<CalibrationBin> Reliability(IReadOnlyList<double> probabilities, IReadOnlyList<int> ys, int bins = 10)
        {
            var result = new List<CalibrationBin>();

            for (var b = 0; b < bins; b++)
            {
                var lower = (double)b / bins;
                var upper = (double)(b + 1) / bins;
                var members = Enumerable.Range(0, probabilities.Count)
                    .Where(i => probabilities[i] >= lower && (probabilities[i] < upper || (b == bins - 1 && probabilities[i] <= upper)))
                    .ToList();

                if (members.Count > 0)
                {
                    result.Add(new CalibrationBin(lower, upper, members.Count, members.Average(i => probabilities[i]), members.Average(i => (double)ys[i])));
                }
            }

            return result;
        }

        public static double ExpectedCalibrationError(IReadOnlyList<double> probabilities, IReadOnlyList<int> ys, int bins = 10)
        {
            var total = probabilities.Count;
            return Reliability(probabilities, ys, bins).Sum(b => (double)b.Count / total * Math.Abs(b.MeanPredicted - b.Observed));
        }
    }

    // Grouped k-fold over spatial blocks: every row of a block lands in the same fold, so a model is
    // never scored on plots that sit next to the ones it learned from
    public static int[][] SpatialFolds(IReadOnlyList<string> blocks, int folds, int seed = 7)
    {
        var distinct = blocks.Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
        var random = new Random(seed);
        var shuffled = distinct.OrderBy(_ => random.Next()).ToList();
        var foldOfBlock = shuffled.Select((block, i) => (block, fold: i % folds)).ToDictionary(t => t.block, t => t.fold);

        return Enumerable.Range(0, folds)
            .Select(f => Enumerable.Range(0, blocks.Count).Where(i => foldOfBlock[blocks[i]] == f).ToArray())
            .ToArray();
    }
}
