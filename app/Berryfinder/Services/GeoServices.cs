using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

public sealed record Place(string Name, string Detail, double Lat, double Lon);

// Place search through OpenStreetMap Nominatim, limited to Finland. The public instance allows one
// request per second and asks for caching, so results are cached and calls are spaced.
public static class Geocoder
{
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<Place> Places)> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    public static async Task<IReadOnlyList<Place>> SearchAsync(string query, string language, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length < 2)
        {
            return [];
        }

        var key = $"{language}:{query.ToLowerInvariant()}";
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromDays(7))
        {
            return hit.Places;
        }

        await Gate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromSeconds(1.1) - (DateTime.UtcNow - _last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _last = DateTime.UtcNow;
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(query)}&countrycodes=fi&format=jsonv2&limit=6&accept-language={language}";
            var json = JsonNode.Parse(await Http.GetStringWithRetryAsync(url, ct, attempts: 2))?.AsArray() ?? [];
            var places = json.Select(p => new Place(
                    p?["name"]?.GetValue<string>() is { Length: > 0 } n ? n : p?["display_name"]?.GetValue<string>() ?? query,
                    p?["display_name"]?.GetValue<string>() ?? "",
                    double.Parse(p?["lat"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture),
                    double.Parse(p?["lon"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture)))
                .ToList();
            Cache[key] = (DateTime.UtcNow, places);
            return places;
        }
        finally
        {
            Gate.Release();
        }
    }
}

public static class ReverseGeocoder
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    // A friendly name for a spot ("Kiiminki", "Haukipudas"): the nearest suburb, village or town. Null when none is found.
    // Cached by ~1 km bucket because the public Nominatim service allows one request per second.
    public static async Task<string?> NameAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{lat:0.00}:{lon:0.00}");
        if (Cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out hit))
            {
                return hit;
            }

            var wait = TimeSpan.FromSeconds(1.1) - (DateTime.UtcNow - _last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _last = DateTime.UtcNow;
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://nominatim.openstreetmap.org/reverse?lat={lat:0.#####}&lon={lon:0.#####}&format=jsonv2&zoom=14&addressdetails=1&accept-language=fi,en");
            var address = JsonNode.Parse(await Http.GetStringWithRetryAsync(url, ct, attempts: 2))?["address"];
            string? name = null;
            foreach (var field in new[] { "suburb", "village", "hamlet", "town", "city_district", "city", "municipality" })
            {
                if (address?[field]?.GetValue<string>() is { Length: > 0 } value)
                {
                    name = value;
                    break;
                }
            }

            if (name is not null)
            {
                if (Cache.Count > 5000)
                {
                    Cache.Clear();
                }

                Cache[key] = name;
            }

            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}

public sealed record DriveEstimate(double Minutes, double Kilometres);

// Driving times on the road network from the OSRM routing engine (OpenStreetMap data). The public
// demo server is for light use only; production should run its own OSRM or a commercial router. When
// routing fails, callers show "not available", never a straight-line figure dressed up as a route.
public static class Router
{
    public static string Endpoint { get; set; } = "https://router.project-osrm.org";

    public static async Task<IReadOnlyList<DriveEstimate?>> DriveTimesAsync((double Lat, double Lon) from, IReadOnlyList<(double Lat, double Lon)> to, CancellationToken ct = default)
    {
        if (to.Count == 0)
        {
            return [];
        }

        try
        {
            var coords = string.Join(';', new[] { from }.Concat(to).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Lon:0.######},{p.Lat:0.######}")));
            var url = $"{Endpoint}/table/v1/driving/{coords}?sources=0&annotations=duration,distance";
            var root = JsonNode.Parse(await Http.GetStringWithRetryAsync(url, ct, attempts: 2));

            if (root?["code"]?.GetValue<string>() != "Ok")
            {
                return to.Select(_ => (DriveEstimate?)null).ToList();
            }

            var durations = root["durations"]![0]!.AsArray();
            var distances = root["distances"]![0]!.AsArray();
            return Enumerable.Range(1, to.Count)
                .Select(i => durations[i] is null || distances[i] is null
                    ? null
                    : new DriveEstimate(durations[i]!.GetValue<double>() / 60.0, distances[i]!.GetValue<double>() / 1000.0))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Instance.Warning($"Routing unavailable: {ex.Message}");
            return to.Select(_ => (DriveEstimate?)null).ToList();
        }
    }

    // Hand-off to the phone's own navigation app, aimed at the access point on the road, not into the forest
    public static string NavigationUrl(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"https://www.google.com/maps/dir/?api=1&destination={lat:0.######},{lon:0.######}&travelmode=driving");

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
