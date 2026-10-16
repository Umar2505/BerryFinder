using System.Globalization;
using System.Text.Json;

// Pure parsing and grid addressing for NASA POWER responses (no network, no database), so it is unit-tested
public static class NasaPowerParser
{
    public sealed record DailyValue(DateOnly Day, double? TMean, double? TMin, double? Precip, double? RootWetness, double? SolarMj);

    // POWER snaps any point to its grid cell; keying by the snapped centre keeps one row set per cell
    public static (double Lat, double Lon) CellOf(double lat, double lon) => (Math.Round(lat * 2) / 2, Math.Round(lon * 1.6) / 1.6);

    public static string GridId(double lat, double lon)
    {
        var (cellLat, cellLon) = CellOf(lat, lon);
        return string.Create(CultureInfo.InvariantCulture, $"power:{cellLat:0.0}:{cellLon:0.000}");
    }

    public static List<DailyValue> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("properties", out var props) || !props.TryGetProperty("parameter", out var parameter))
        {
            return [];
        }

        // POWER marks missing values with a fill value (-999 by default)
        var fill = root.TryGetProperty("header", out var header) && header.TryGetProperty("fill_value", out var f) && f.TryGetDouble(out var fv) ? fv : -999;

        double? Value(string name, string key) =>
            parameter.TryGetProperty(name, out var series) && series.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.GetDouble() is var d && d != fill && d > -990 ? d : null;

        if (!parameter.TryGetProperty("T2M", out var t2m))
        {
            return [];
        }

        return t2m.EnumerateObject()
            .Select(p => (Key: p.Name, Date: DateOnly.TryParseExact(p.Name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null))
            .Where(x => x.Date is not null)
            .Select(x => new DailyValue(x.Date!.Value, Value("T2M", x.Key), Value("T2M_MIN", x.Key), Value("PRECTOTCORR", x.Key), Value("GWETROOT", x.Key), Value("ALLSKY_SFC_SW_DWN", x.Key)))
            .Where(d => d.TMean is not null || d.Precip is not null)
            .OrderBy(d => d.Day)
            .ToList();
    }
}
