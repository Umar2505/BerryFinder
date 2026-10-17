using System.Net;
using System.Text;
using System.Text.Json;

// Client for the BerryFinder inference service (inference/, Python + the trained PyTorch model). The app never runs the model
// itself: it starts an analysis there, follows its real stages, and hands the georeferenced result to the map.
public sealed record AnalysisProgress(string? Stage, string? Label, int CompletedStages);

public sealed record AnalysisError(string Code, string Message);

public sealed record AnalysisPin(string Id, string Species, int Rank, double Lat, double Lon, double LocalRank, double NordicIndex, string Band,
    int Cells, double AreaHa, double Uncertainty, double DistanceM, string HabitatEn, string HabitatFi);

public sealed record AnalysisSpecies(string Species, int CandidateAreas, double AreaIndex, string AreaBand, double LocalContrast, string LocalDifferences);

public sealed record AnalysisSummary(string ModelVersion, string? LabelMode, int StepM, int LandCells, double AreaKm2, IReadOnlyList<AnalysisSpecies> Species,
    IReadOnlyList<AnalysisPin> Pins, IReadOnlyDictionary<string, string> DataSources, string ComputedAt);

public sealed record AnalysisJob(string JobId, string Status, AnalysisProgress Progress, AnalysisError? Error, AnalysisSummary? Summary);

public sealed record BerryType(string Id, string NameEn, string NameFi, bool Supported, double? HeldOutAuc);

public sealed class InferenceUnavailableException(string message) : Exception(message);

public sealed class InferenceRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class InferenceClient(string baseUrl)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    public async Task<AnalysisJob> StartAsync(double lat, double lon, int radiusM, IReadOnlyList<string>? species, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { lat, lon, radius_m = radiusM, species });
        using var response = await SendAsync(() => Client.PostAsync($"{BaseUrl}/api/v1/predictions", new StringContent(body, Encoding.UTF8, "application/json"), ct));
        return ParseJob(await ReadOkAsync(response, ct));
    }

    public async Task<AnalysisJob> StatusAsync(string jobId, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => Client.GetAsync($"{BaseUrl}/api/v1/predictions/{Uri.EscapeDataString(jobId)}?include=summary", ct));
        return ParseJob(await ReadOkAsync(response, ct));
    }

    // The full result (cells and pins as GeoJSON) for the map, passed through as it came
    public async Task<string?> GeoJsonAsync(string jobId, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => Client.GetAsync($"{BaseUrl}/api/v1/predictions/{Uri.EscapeDataString(jobId)}", ct));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await ReadOkAsync(response, ct));
        if (!doc.RootElement.TryGetProperty("result", out var result))
        {
            return null;
        }

        return JsonSerializer.Serialize(new { cells = result.GetProperty("cells_geojson"), pins = result.GetProperty("pins_geojson") });
    }

    public async Task<IReadOnlyList<BerryType>> BerryTypesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => Client.GetAsync($"{BaseUrl}/api/v1/berries/types", ct));
        using var doc = JsonDocument.Parse(await ReadOkAsync(response, ct));
        return doc.RootElement.GetProperty("berries").EnumerateArray().Select(b => new BerryType(
            b.GetProperty("id").GetString()!, b.GetProperty("name_en").GetString()!, b.GetProperty("name_fi").GetString()!,
            b.GetProperty("supported").GetBoolean(), b.TryGetProperty("held_out_auc", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null)).ToList();
    }

    public async Task<bool> HealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync($"{BaseUrl}/api/v1/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InferenceUnavailableException(ex.Message);
        }
    }

    private static async Task<string> ReadOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return text;
        }

        if ((int)response.StatusCode >= 500 && (int)response.StatusCode != 503)
        {
            throw new InferenceUnavailableException($"HTTP {(int)response.StatusCode}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var e = doc.RootElement.GetProperty("error");
            throw new InferenceRequestException(e.GetProperty("code").GetString() ?? "error", e.GetProperty("message").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InferenceUnavailableException($"HTTP {(int)response.StatusCode}");
        }
    }

    public static AnalysisJob ParseJob(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var p = root.GetProperty("progress");
        var progress = new AnalysisProgress(Str(p, "stage"), Str(p, "label"), p.TryGetProperty("completed_stages", out var c) ? c.GetInt32() : 0);
        AnalysisError? error = root.TryGetProperty("error", out var e) ? new AnalysisError(Str(e, "code") ?? "error", Str(e, "message") ?? "") : null;
        AnalysisSummary? summary = null;
        if (root.TryGetProperty("result", out var r))
        {
            var pins = r.GetProperty("pins").EnumerateArray().Select(x => new AnalysisPin(
                Str(x, "id")!, Str(x, "species")!, x.GetProperty("rank").GetInt32(), Num(x, "lat"), Num(x, "lon"), Num(x, "local_rank"), Num(x, "nordic_index"),
                Str(x, "band")!, x.GetProperty("cells").GetInt32(), Num(x, "area_ha"), Num(x, "uncertainty"), Num(x, "distance_m"),
                Str(x.GetProperty("habitat"), "en") ?? "", Str(x.GetProperty("habitat"), "fi") ?? "")).ToList();
            var species = r.GetProperty("summary").EnumerateArray().Select(x => new AnalysisSpecies(
                Str(x, "species")!, x.GetProperty("candidate_areas").GetInt32(), Num(x, "area_index"), Str(x, "area_band")!, Num(x, "local_contrast"),
                Str(x, "local_differences")!)).ToList();
            var sources = r.TryGetProperty("data_sources", out var ds) && ds.ValueKind == JsonValueKind.Object
                ? ds.EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString() ?? "")
                : new Dictionary<string, string>();
            var model = r.GetProperty("model");
            summary = new AnalysisSummary(Str(model, "version") ?? "", Str(model, "label_mode"), r.GetProperty("grid").GetProperty("step_m").GetInt32(),
                r.GetProperty("grid").GetProperty("land_cells").GetInt32(), Num(r, "area_km2"), species, pins, sources, Str(r, "computed_at") ?? "");
        }

        return new AnalysisJob(Str(root, "job_id")!, Str(root, "status")!, progress, error, summary);

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }
}
