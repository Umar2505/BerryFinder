using System.Globalization;
using Xunit;
using Xunit.Abstractions;

// Validates the linear solver that sits inside the logistic-regression fit against certified answers from
// the NIST Statistical Reference Datasets (StRD, https://www.itl.nist.gov/div898/strd/), a U.S. government
// resource. The data files are NIST's own, unedited; the certified values are read from the same files.
public class NistReferenceTests(ITestOutputHelper output)
{
    private sealed record Certified(double[] Beta, double[] StdErr, double ResidualSd, double[][] X, double[] Y);

    private static Certified Load(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nist", file));
        var beta = new List<double>();
        var stderr = new List<double>();
        double residualSd = 0;
        var inData = false;
        var xs = new List<double[]>();
        var ys = new List<double>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (inData)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    ys.Add(y);
                    xs.Add(parts.Skip(1).Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray());
                }

                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^Data:\s+y\s+x"))
            {
                inData = true;
                continue;
            }

            var cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length == 3 && cells[0].Length >= 2 && cells[0][0] == 'B' && char.IsDigit(cells[0][1]))
            {
                beta.Add(double.Parse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture));
                stderr.Add(double.Parse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            else if (line.StartsWith("Standard Deviation") && cells.Length >= 3)
            {
                residualSd = double.Parse(cells[^1], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        return new Certified([.. beta], [.. stderr], residualSd, [.. xs], [.. ys]);
    }

    // Ordinary least squares through the normal equations, solved with the production solver
    private static double[] Fit(Certified data)
    {
        var p = data.X[0].Length + 1;
        var xtx = new double[p, p];
        var xty = new double[p];

        for (var i = 0; i < data.Y.Length; i++)
        {
            var row = new double[p];
            row[0] = 1;
            Array.Copy(data.X[i], 0, row, 1, p - 1);

            for (var a = 0; a < p; a++)
            {
                xty[a] += row[a] * data.Y[i];
                for (var b = 0; b < p; b++)
                {
                    xtx[a, b] += row[a] * row[b];
                }
            }
        }

        return Ml.SolveLinearSystem(xtx, xty);
    }

    // Correct significant digits, as NIST's own LRE (log relative error) measure defines them
    private static double Lre(double estimate, double certified) =>
        certified == 0 ? -Math.Log10(Math.Abs(estimate) + 1e-300) : -Math.Log10(Math.Abs(estimate - certified) / Math.Abs(certified) + 1e-16);

    [Fact]
    public void Norris_matches_certified_coefficients_to_at_least_9_digits()
    {
        var data = Load("norris.dat");
        Assert.Equal(2, data.Beta.Length);
        Assert.Equal(36, data.Y.Length);

        var estimate = Fit(data);
        for (var i = 0; i < estimate.Length; i++)
        {
            var digits = Lre(estimate[i], data.Beta[i]);
            output.WriteLine($"Norris B{i}: {estimate[i]:R} vs certified {data.Beta[i]:R} → {digits:0.0} digits");
            Assert.True(digits >= 9, $"B{i} agreed to only {digits:0.0} digits");
        }
    }

    [Fact]
    public void Norris_residual_standard_deviation_matches_certified()
    {
        var data = Load("norris.dat");
        var beta = Fit(data);
        var sse = data.Y.Select((y, i) => Math.Pow(y - beta[0] - beta[1] * data.X[i][0], 2)).Sum();
        var sd = Math.Sqrt(sse / (data.Y.Length - 2));
        Assert.True(Lre(sd, data.ResidualSd) >= 9, $"Residual SD {sd:R} vs certified {data.ResidualSd:R}");
    }

    // Longley is NIST's classic ill-conditioned problem (condition number around 1e9 after the intercept).
    // Normal equations are expected to lose digits here, so the bar is lower and the result is reported.
    [Fact]
    public void Longley_ill_conditioned_regression_keeps_at_least_5_digits()
    {
        var data = Load("longley.dat");
        Assert.Equal(7, data.Beta.Length);
        Assert.Equal(16, data.Y.Length);

        var estimate = Fit(data);
        var worst = double.MaxValue;
        for (var i = 0; i < estimate.Length; i++)
        {
            var digits = Lre(estimate[i], data.Beta[i]);
            output.WriteLine($"Longley B{i}: {estimate[i]:R} vs certified {data.Beta[i]:R} → {digits:0.0} digits");
            worst = Math.Min(worst, digits);
        }

        Assert.True(worst >= 5, $"Worst coefficient agreed to only {worst:0.0} digits");
    }

    [Fact]
    public void Solver_reports_singular_systems_without_producing_NaN()
    {
        var a = new double[,] { { 1, 2 }, { 2, 4 } };
        var x = Ml.SolveLinearSystem(a, [1, 2]);
        Assert.All(x, v => Assert.True(double.IsFinite(v)));
    }
}
