using System.Globalization;
using System.Xml.Linq;

// Pure parser for FMI's daily simple-feature WFS output (no network, no database), so it is unit-tested
public static class FmiParser
{
    public sealed record DailyValue(DateOnly Day, double? TMean, double? TMin, double? TMax, double? Precip, double? Snow, double? Ground);

    public static ((double Lat, double Lon)? Station, List<DailyValue> Days) Parse(string xml)
    {
        XNamespace bs = "http://xml.fmi.fi/schema/wfs/2.0";
        XNamespace gml = "http://www.opengis.net/gml/3.2";
        var doc = XDocument.Parse(xml);
        (double, double)? station = null;
        var values = new Dictionary<DateOnly, Dictionary<string, double?>>();

        foreach (var element in doc.Descendants(bs + "BsWfsElement"))
        {
            var pos = element.Descendants(gml + "pos").FirstOrDefault()?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (station is null && pos is { Length: >= 2 })
            {
                station = (double.Parse(pos[0], CultureInfo.InvariantCulture), double.Parse(pos[1], CultureInfo.InvariantCulture));
            }

            var time = element.Element(bs + "Time")?.Value;
            var name = element.Element(bs + "ParameterName")?.Value;
            var raw = element.Element(bs + "ParameterValue")?.Value;

            if (time is null || name is null || raw is null)
            {
                continue;
            }

            var day = DateOnly.FromDateTime(DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
            double? value = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;

            if (!values.TryGetValue(day, out var row))
            {
                values[day] = row = [];
            }

            row[name.ToLowerInvariant()] = value;
        }

        // FMI reports -1 for "no precipitation" and "no snow"; those are zeros, not missing values
        static double? NonNegative(double? v) => v is < 0 ? 0 : v;

        var days = values.OrderBy(kv => kv.Key).Select(kv => new DailyValue(kv.Key,
            kv.Value.GetValueOrDefault("tday"), kv.Value.GetValueOrDefault("tmin"), kv.Value.GetValueOrDefault("tmax"),
            NonNegative(kv.Value.GetValueOrDefault("rrday")), NonNegative(kv.Value.GetValueOrDefault("snow")), kv.Value.GetValueOrDefault("tg_pt12h_min")))
            .Where(d => d.TMean is not null || d.Precip is not null)
            .ToList();

        return (station, days);
    }
}
