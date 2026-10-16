using System.Runtime.CompilerServices;

public sealed record MapView(double Lat, double Lon, double Zoom, int Key);

public sealed record RankedPin(long Id, double Lat, double Lon, int Rank);

public sealed record UserLocation(double Lat, double Lon, double Accuracy);

public sealed record LatLon(double Lat, double Lon);

// C# side of the MapLibre map in frontend-node/src/lib/berry-map. Events come back as JSON strings.
public static class BerryMapExtensions
{
    public static void BerryMap(
        this UIView view,
        string tileUrl,
        string species,
        int dataVersion,
        string attribution,
        string locateLabel,
        MapView? viewRequest = null,
        string basemap = "map",
        string? satelliteUrl = null,
        bool showForest = false,
        bool showSoil = false,
        long? selectedAreaId = null,
        IReadOnlyList<RankedPin>? rankedAreas = null,
        UserLocation? userLocation = null,
        bool pickMode = false,
        LatLon? pickedPoint = null,
        string? coverage = null,
        Func<string, Task>? onViewChange = null,
        Func<string, Task>? onSelectArea = null,
        Func<string, Task>? onSelectStand = null,
        Func<string, Task>? onLocate = null,
        Func<string, Task>? onPick = null,
        string[]? style = null,
        string? key = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var props = new Dictionary<string, object?>
        {
            ["tileUrl"] = tileUrl,
            ["species"] = species,
            ["dataVersion"] = dataVersion,
            ["attribution"] = attribution,
            ["locateLabel"] = locateLabel,
            ["view"] = viewRequest,
            ["basemap"] = basemap,
            ["satelliteUrl"] = satelliteUrl,
            ["showForest"] = showForest,
            ["showSoil"] = showSoil,
            ["selectedAreaId"] = selectedAreaId,
            ["rankedAreas"] = rankedAreas ?? [],
            ["userLocation"] = userLocation,
            ["pickMode"] = pickMode,
            ["pickedPoint"] = pickedPoint,
            ["coverage"] = coverage,
        };

        if (onViewChange is not null) props["onViewChangeId"] = view.CreateAction<string>(args => onViewChange(args.Value ?? ""));
        if (onSelectArea is not null) props["onSelectAreaId"] = view.CreateAction<string>(args => onSelectArea(args.Value ?? ""));
        if (onSelectStand is not null) props["onSelectStandId"] = view.CreateAction<string>(args => onSelectStand(args.Value ?? ""));
        if (onLocate is not null) props["onLocateId"] = view.CreateAction<string>(args => onLocate(args.Value ?? ""));
        if (onPick is not null) props["onPickId"] = view.CreateAction<string>(args => onPick(args.Value ?? ""));

        view.AddNode(type: "berry-map", props: props, style: style, key: key, file: file, line: line);
    }
}
