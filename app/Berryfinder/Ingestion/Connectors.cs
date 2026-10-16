using System.Data.Common;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;

// Opens an open database connection; each operation opens its own and disposes it
public delegate Task<DbConnection> OpenDb();

public sealed record PilotTile(string Id, int XMin, int YMin, int XMax, int YMax, double West, double South, double East, double North);

// What a running job reports back: a fraction done and a line of status
public sealed class JobProgress(Func<double, string, Task> report)
{
    public int RecordsIn { get; set; }
    public int RecordsUpserted { get; set; }
    public List<string> Warnings { get; } = [];
    public Task ReportAsync(double fraction, string message) => report(fraction, message);
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        // Overpass and Nominatim policies require an identifying user agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BerryFinderAI/0.1 (+https://berryfinder.ikonai.app; pilot research service)");
        return client;
    }

    public static async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await Client.GetAsync(url, ct);
                if ((int)response.StatusCode is 429 or >= 500 && attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }
}

// Finnish Forest Centre stands. Parsing happens in PostGIS from the raw GeoJSON, so a renamed or
// dropped attribute becomes NULL instead of a crash, and the job reports which expected keys were missing.
public static class MetsakeskusConnector
{
    public const string SchemaVersion = "metsakeskus-v2-stand";
    private const string Endpoint = "https://avoin.metsakeskus.fi/rajapinnat/v2/stand/ows";
    private const int PageSize = 1000;

    private static readonly string[] ExpectedKeys =
    [
        "standid", "maingroup", "subgroup", "fertilityclass", "soiltype", "drainagestate", "developmentclass", "maintreespecies",
        "proportionpine", "proportionspruce", "proportionother", "meanage", "basalarea", "meanheight", "measurementdate", "updatetime",
    ];

    public static async Task IngestTileAsync(OpenDb openDb, PilotTile tile, JobProgress progress, CancellationToken ct)
    {
        var total = 0;

        for (var start = 0; ; start += PageSize)
        {
            var url = $"{Endpoint}?service=WFS&version=2.0.0&request=GetFeature&typeNames=v2:stand&outputFormat=application/json&srsName=EPSG:3067" +
                      $"&bbox={tile.XMin},{tile.YMin},{tile.XMax},{tile.YMax},EPSG:3067&count={PageSize}&startIndex={start}&sortBy=standid";
            var json = await Http.GetStringWithRetryAsync(url, ct);
            var count = CountFeaturesAndCheckSchema(json, progress);

            if (count > 0)
            {
                await using var db = await openDb();
                total += await db.ExecuteAsync(new CommandDefinition(UpsertSql, new { fc = json, tile = tile.Id, schema = SchemaVersion }, commandTimeout: 300, cancellationToken: ct));
            }

            progress.RecordsIn += count;

            if (count < PageSize)
            {
                break;
            }
        }

        await using (var db = await openDb())
        {
            await db.ExecuteAsync("UPDATE pilot_tiles SET stands_ingested_at = now(), stand_count = (SELECT count(*) FROM forest_stands WHERE tile_id = @id) WHERE id = @id", new { id = tile.Id });
        }

        progress.RecordsUpserted += total;
    }

    private static int CountFeaturesAndCheckSchema(string json, JobProgress progress)
    {
        using var doc = JsonDocument.Parse(json);
        var features = doc.RootElement.GetProperty("features");

        if (features.GetArrayLength() > 0 && features[0].TryGetProperty("properties", out var props))
        {
            var missing = ExpectedKeys.Where(k => !props.TryGetProperty(k, out _)).ToList();
            if (missing.Count > 0)
            {
                var warning = $"Metsäkeskus schema changed: missing {string.Join(", ", missing)}";
                if (!progress.Warnings.Contains(warning))
                {
                    progress.Warnings.Add(warning);
                }
            }
        }

        return features.GetArrayLength();
    }

    // Stands crossing a tile edge come back for both tiles; the upsert keeps one row per standid
    private const string UpsertSql = """
        WITH f AS (
            SELECT feature, ST_CollectionExtract(ST_MakeValid(ST_SetSRID(ST_GeomFromGeoJSON(feature->>'geometry'), 3067)), 3) AS g
            FROM jsonb_array_elements(CAST(@fc AS jsonb) -> 'features') AS feature
            WHERE feature->'properties'->>'standid' IS NOT NULL AND feature->'geometry' IS NOT NULL AND feature->'geometry' <> 'null'::jsonb
        ), p AS (
            SELECT feature->'properties' AS p, ST_Multi(g) AS g FROM f WHERE NOT ST_IsEmpty(g)
        )
        INSERT INTO forest_stands (stand_id, municipality, land_class, site_main_class, fertility_class, soil_type, drainage_state, development_class,
            main_species, prop_pine, prop_spruce, prop_other, mean_age, basal_area, stem_count, mean_diameter, mean_height, volume, cutting_type,
            cutting_proposal_year, data_source_code, tree_data_date, measurement_date, source_created_at, source_updated_at, area_ha,
            geom, geom_web, centroid, tile_id, ingested_at, schema_version)
        SELECT (p->>'standid')::bigint, p->>'municipalitynumber', p->>'maingroup', p->>'subgroup',
            NULLIF(p->>'fertilityclass', '')::smallint, NULLIF(p->>'soiltype', '')::smallint, p->>'drainagestate', p->>'developmentclass',
            NULLIF(p->>'maintreespecies', '')::smallint, (p->>'proportionpine')::real, (p->>'proportionspruce')::real, (p->>'proportionother')::real,
            (p->>'meanage')::real, (p->>'basalarea')::real, (p->>'stemcount')::int, (p->>'meandiameter')::real, (p->>'meanheight')::real, (p->>'volume')::real,
            p->>'cuttingtype', (p->>'cuttingproposalyear')::numeric::int, p->>'treestanddatasource',
            (p->>'treestanddatadate')::timestamptz::date, (p->>'measurementdate')::timestamptz::date,
            (p->>'creationtime')::timestamptz, (p->>'updatetime')::timestamptz, (p->>'area')::real,
            g, ST_Multi(ST_Transform(g, 3857)), ST_Transform(ST_PointOnSurface(g), 4326), @tile, now(), @schema
        FROM p
        ON CONFLICT (stand_id) DO UPDATE SET
            municipality = EXCLUDED.municipality, land_class = EXCLUDED.land_class, site_main_class = EXCLUDED.site_main_class,
            fertility_class = EXCLUDED.fertility_class, soil_type = EXCLUDED.soil_type, drainage_state = EXCLUDED.drainage_state,
            development_class = EXCLUDED.development_class, main_species = EXCLUDED.main_species, prop_pine = EXCLUDED.prop_pine,
            prop_spruce = EXCLUDED.prop_spruce, prop_other = EXCLUDED.prop_other, mean_age = EXCLUDED.mean_age, basal_area = EXCLUDED.basal_area,
            stem_count = EXCLUDED.stem_count, mean_diameter = EXCLUDED.mean_diameter, mean_height = EXCLUDED.mean_height, volume = EXCLUDED.volume,
            cutting_type = EXCLUDED.cutting_type, cutting_proposal_year = EXCLUDED.cutting_proposal_year, data_source_code = EXCLUDED.data_source_code,
            tree_data_date = EXCLUDED.tree_data_date, measurement_date = EXCLUDED.measurement_date, source_created_at = EXCLUDED.source_created_at,
            source_updated_at = EXCLUDED.source_updated_at, area_ha = EXCLUDED.area_ha, geom = EXCLUDED.geom, geom_web = EXCLUDED.geom_web,
            centroid = EXCLUDED.centroid, ingested_at = now(), schema_version = EXCLUDED.schema_version
        """;
}

// GTK superficial deposits at three map scales. The class is decided in C# (SoilClassifier) and the
// geometry is parsed in PostGIS.
public static class GtkSoilConnector
{
    private const string Endpoint = "https://gtkdata.gtk.fi/arcgis/services/Rajapinnat/GTK_Maapera_WFS/MapServer/WFSServer";
    private static readonly (string Layer, short Rank)[] Layers = [("maapera_20k_maalajit", 1), ("maapera_50k_maalajit", 2), ("maapera_200k_maalajit", 4)];

    public static async Task IngestTileAsync(OpenDb openDb, PilotTile tile, JobProgress progress, CancellationToken ct)
    {
        foreach (var (layer, rank) in Layers)
        {
            var url = $"{Endpoint}?service=WFS&version=2.0.0&request=GetFeature&typeNames=Rajapinnat_GTK_Maapera_WFS:{layer}&outputFormat=GEOJSON" +
                      $"&srsName=urn:ogc:def:crs:EPSG::3067&bbox={tile.XMin},{tile.YMin},{tile.XMax},{tile.YMax},urn:ogc:def:crs:EPSG::3067&count=5000";
            var json = await Http.GetStringWithRetryAsync(url, ct);
            var root = JsonNode.Parse(json)?.AsObject();
            var features = root?["features"]?.AsArray();

            if (features is null || features.Count == 0)
            {
                continue;
            }

            var rows = new JsonArray();
            foreach (var feature in features)
            {
                var props = feature?["properties"];
                var surface = props?["PINTAMAALAJI"]?.GetValue<string>();
                rows.Add(new JsonObject
                {
                    ["oid"] = props?["OBJECTID"]?.DeepClone(),
                    ["code"] = props?["PINTAMAALAJI_KOODI"]?.DeepClone(),
                    ["surface"] = surface,
                    ["base"] = props?["POHJAMAALAJI"]?.DeepClone(),
                    ["cls"] = SoilClassifier.Classify(surface),
                    ["geometry"] = feature?["geometry"]?.DeepClone(),
                });
            }

            progress.RecordsIn += rows.Count;
            await using var db = await openDb();
            progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                WITH r AS (SELECT x FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS x WHERE x->>'oid' IS NOT NULL AND x->'geometry' <> 'null'::jsonb),
                g AS (SELECT x, ST_Multi(ST_CollectionExtract(ST_MakeValid(ST_SetSRID(ST_GeomFromGeoJSON(x->>'geometry'), 3067)), 3)) AS geom FROM r)
                INSERT INTO soil_polygons (source_layer, scale_rank, source_object_id, surface_code, surface_label, base_label, soil_class, geom)
                SELECT @layer, @rank, (x->>'oid')::bigint, (x->>'code')::bigint, x->>'surface', x->>'base', x->>'cls', geom FROM g WHERE NOT ST_IsEmpty(geom)
                ON CONFLICT (source_layer, source_object_id) DO UPDATE SET surface_code = EXCLUDED.surface_code, surface_label = EXCLUDED.surface_label,
                    base_label = EXCLUDED.base_label, soil_class = EXCLUDED.soil_class, geom = EXCLUDED.geom, ingested_at = now()
                """, new { rows = rows.ToJsonString(), layer, rank }, commandTimeout: 300, cancellationToken: ct));
        }

        await using (var db = await openDb())
        {
            await db.ExecuteAsync("UPDATE pilot_tiles SET soil_ingested_at = now() WHERE id = @id", new { id = tile.Id });
        }
    }
}

// OpenStreetMap ways through the Overpass API, one polite request per tile
public static class OsmAccessConnector
{
    private const string Endpoint = "https://overpass-api.de/api/interpreter";
    private const string Roads = "motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|road";
    private const string Paths = "path|footway|bridleway|cycleway|steps";

    public static async Task IngestTileAsync(OpenDb openDb, PilotTile tile, JobProgress progress, CancellationToken ct)
    {
        var bbox = string.Create(CultureInfo.InvariantCulture, $"{tile.South:0.#####},{tile.West:0.#####},{tile.North:0.#####},{tile.East:0.#####}");
        var query = $"[out:json][timeout:90];way[\"highway\"~\"^({Roads}|track|{Paths})$\"]({bbox});out geom tags;";

        // The public Overpass server answers 429/502/504 under load; back off and retry before giving up on the tile
        string body;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent([new("data", query)]) };
            using var response = await Http.Client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                body = await response.Content.ReadAsStringAsync(ct);
                break;
            }

            if ((int)response.StatusCode is not (429 or 502 or 503 or 504) || attempt >= 4)
            {
                response.EnsureSuccessStatusCode();
            }

            await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
        }

        var root = JsonNode.Parse(body);
        var rows = new JsonArray();

        foreach (var element in root?["elements"]?.AsArray() ?? [])
        {
            var highway = element?["tags"]?["highway"]?.GetValue<string>();
            var geometry = element?["geometry"]?.AsArray();

            if (highway is null || geometry is null || geometry.Count < 2)
            {
                continue;
            }

            var kind = highway == "track" ? "track" : Paths.Split('|').Contains(highway) ? "path" : "road";
            var coordinates = new JsonArray();
            foreach (var point in geometry)
            {
                coordinates.Add(new JsonArray(point!["lon"]!.GetValue<double>(), point["lat"]!.GetValue<double>()));
            }

            rows.Add(new JsonObject
            {
                ["id"] = element!["id"]!.GetValue<long>(),
                ["kind"] = kind,
                ["highway"] = highway,
                ["access"] = element["tags"]?["access"]?.GetValue<string>() ?? element["tags"]?["motor_vehicle"]?.GetValue<string>(),
                ["name"] = element["tags"]?["name"]?.GetValue<string>(),
                ["geometry"] = new JsonObject { ["type"] = "LineString", ["coordinates"] = coordinates },
            });
        }

        progress.RecordsIn += rows.Count;

        await using var db = await openDb();
        progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO access_ways (osm_id, kind, highway, access_tag, name, geom)
            SELECT (x->>'id')::bigint, x->>'kind', x->>'highway', x->>'access', x->>'name', ST_Transform(ST_SetSRID(ST_GeomFromGeoJSON(x->>'geometry'), 4326), 3067)
            FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS x
            ON CONFLICT (osm_id) DO UPDATE SET kind = EXCLUDED.kind, highway = EXCLUDED.highway, access_tag = EXCLUDED.access_tag, name = EXCLUDED.name,
                geom = EXCLUDED.geom, ingested_at = now()
            """, new { rows = rows.ToJsonString() }, commandTimeout: 300, cancellationToken: ct));
        await db.ExecuteAsync("UPDATE pilot_tiles SET access_ingested_at = now() WHERE id = @id", new { id = tile.Id });

        // Overpass asks clients not to hammer the public instance
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}

// SYKE protected areas over the whole pilot extent (two layers: state-owned and private reserves)
public static class SykeProtectedConnector
{
    private const string Endpoint = "https://paikkatiedot.ymparisto.fi/geoserver/inspire_ps/wfs";
    private static readonly string[] Layers = ["PS.ProtectedSitesValtionOmistamaLuonnonsuojelualue", "PS.ProtectedSitesYksityistenMaillaOlevaLuonnonsuojelualue"];

    public static async Task IngestAsync(OpenDb openDb, (int XMin, int YMin, int XMax, int YMax) extent, JobProgress progress, CancellationToken ct)
    {
        foreach (var layer in Layers)
        {
            var url = $"{Endpoint}?service=WFS&version=2.0.0&request=GetFeature&typeNames=inspire_ps:{layer}&outputFormat=application/json&srsName=EPSG:3067" +
                      $"&bbox={extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax},EPSG:3067&count=5000";
            var json = await Http.GetStringWithRetryAsync(url, ct);
            using (var doc = JsonDocument.Parse(json))
            {
                progress.RecordsIn += doc.RootElement.GetProperty("features").GetArrayLength();
            }

            await using var db = await openDb();
            // Strict nature reserves (luonnonpuisto) restrict entry and picking; every other reserve type is a warning to check local rules
            progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                WITH f AS (SELECT x FROM jsonb_array_elements(CAST(@fc AS jsonb) -> 'features') AS x WHERE x->'geometry' <> 'null'::jsonb),
                g AS (SELECT x->'properties' AS p, ST_Multi(ST_CollectionExtract(ST_MakeValid(ST_SetSRID(ST_GeomFromGeoJSON(x->>'geometry'), 3067)), 3)) AS geom FROM f)
                INSERT INTO access_restrictions (source, source_ref, name, restriction_type, severity, legal_basis, notes, geom)
                SELECT 'syke', COALESCE(p->>'lsaluetunnus', p->>'kohdeid', p->>'objectid'), p->>'nimi', COALESCE(p->>'tyyppinimi', 'Protected area'),
                    CASE WHEN lower(COALESCE(p->>'tyyppinimi', '') || ' ' || COALESCE(p->>'nimi', '')) LIKE '%luonnonpuisto%' THEN 'exclude' ELSE 'warn' END,
                    p->>'lakiperuste', @layer, geom
                FROM g WHERE NOT ST_IsEmpty(geom) AND COALESCE(p->>'olotila', 'Voimassa') = 'Voimassa'
                ON CONFLICT (source, source_ref) DO UPDATE SET name = EXCLUDED.name, restriction_type = EXCLUDED.restriction_type,
                    severity = CASE WHEN access_restrictions.created_by IS NULL THEN EXCLUDED.severity ELSE access_restrictions.severity END,
                    legal_basis = EXCLUDED.legal_basis, geom = EXCLUDED.geom
                """, new { fc = json, layer }, commandTimeout: 300, cancellationToken: ct));
        }

        await using (var db = await openDb())
        {
            await db.ExecuteAsync("UPDATE pilot_tiles SET protected_ingested_at = now() WHERE enabled");
        }
    }
}

// FMI daily station observations. Each station's values describe that station only.
public static class FmiWeatherConnector
{
    private const string Endpoint = "https://opendata.fmi.fi/wfs";

    // Stations nearest the Oulu pilot (FMISID): Oulu airport and Oulu Vihreäsaari harbour
    public static readonly int[] PilotStations = [101786, 101794];

    public static async Task IngestAsync(OpenDb openDb, int years, JobProgress progress, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var fmisid in PilotStations)
        {
            for (var year = today.Year - years; year <= today.Year; year++)
            {
                var from = new DateTime(year, 3, 25, 0, 0, 0, DateTimeKind.Utc);
                var to = year == today.Year ? DateTime.UtcNow.Date : new DateTime(year, 11, 15, 0, 0, 0, DateTimeKind.Utc);

                // Skip seasons already stored in full; the current season always refreshes
                await using (var check = await openDb())
                {
                    var stored = await check.ExecuteScalarAsync<int>("SELECT count(*) FROM weather_daily WHERE fmisid = @fmisid AND day BETWEEN @a AND @b",
                        new { fmisid, a = DateOnly.FromDateTime(from), b = DateOnly.FromDateTime(to) });
                    if (year < today.Year && stored >= (to - from).TotalDays - 3)
                    {
                        continue;
                    }
                }

                var url = $"{Endpoint}?service=WFS&version=2.0.0&request=getFeature&storedquery_id=fmi::observations::weather::daily::simple" +
                          $"&fmisid={fmisid}&starttime={from:yyyy-MM-ddTHH:mm:ssZ}&endtime={to:yyyy-MM-ddTHH:mm:ssZ}&parameters=tday,tmin,tmax,rrday,snow,TG_PT12H_min";
                var xml = await Http.GetStringWithRetryAsync(url, ct);
                var (station, days) = FmiParser.Parse(xml);

                if (station is null || days.Count == 0)
                {
                    progress.Warnings.Add($"FMI station {fmisid} returned no observations for {year}");
                    continue;
                }

                progress.RecordsIn += days.Count;
                await using var db = await openDb();
                await db.ExecuteAsync("""
                    INSERT INTO weather_stations (fmisid, name, geom) VALUES (@fmisid, @name, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326))
                    ON CONFLICT (fmisid) DO UPDATE SET geom = EXCLUDED.geom
                    """, new { fmisid, name = StationName(fmisid), lat = station.Value.Lat, lon = station.Value.Lon });

                progress.RecordsUpserted += await db.ExecuteAsync("""
                    INSERT INTO weather_daily (fmisid, day, t_mean, t_min, t_max, precip_mm, snow_cm, ground_min)
                    VALUES (@Fmisid, @Day, @TMean, @TMin, @TMax, @Precip, @Snow, @Ground)
                    ON CONFLICT (fmisid, day) DO UPDATE SET t_mean = EXCLUDED.t_mean, t_min = EXCLUDED.t_min, t_max = EXCLUDED.t_max,
                        precip_mm = EXCLUDED.precip_mm, snow_cm = EXCLUDED.snow_cm, ground_min = EXCLUDED.ground_min, fetched_at = now()
                    """, days.Select(d => new { Fmisid = fmisid, d.Day, d.TMean, d.TMin, d.TMax, d.Precip, d.Snow, d.Ground }));
            }
        }
    }

    public static string StationName(int fmisid) => fmisid switch
    {
        101786 => "Oulu lentoasema (airport)",
        101794 => "Oulu Vihreäsaari satama",
        _ => $"FMI station {fmisid}",
    };
}

// Sentinel-2 L2A scene discovery through the Copernicus STAC API (metadata only; no pixels)
public static class SentinelStacConnector
{
    private const string Endpoint = "https://stac.dataspace.copernicus.eu/v1/search";

    public static async Task IngestAsync(OpenDb openDb, (double West, double South, double East, double North) bbox, int seasons, JobProgress progress, CancellationToken ct)
    {
        var today = DateTime.UtcNow;

        for (var year = today.Year - seasons + 1; year <= today.Year; year++)
        {
            var body = new JsonObject
            {
                ["collections"] = new JsonArray("sentinel-2-l2a"),
                ["bbox"] = new JsonArray(bbox.West, bbox.South, bbox.East, bbox.North),
                ["datetime"] = $"{year}-05-15T00:00:00Z/{year}-09-15T23:59:59Z",
                ["limit"] = 100,
            };
            string? next = Endpoint;
            var page = 0;

            while (next is not null && page++ < 20)
            {
                using var response = page == 1
                    ? await Http.Client.PostAsync(next, JsonContent.Create(body), ct)
                    : await Http.Client.GetAsync(next, ct);
                response.EnsureSuccessStatusCode();
                var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                var features = root["features"]?.AsArray() ?? [];
                var rows = new JsonArray();

                foreach (var f in features)
                {
                    var props = f?["properties"];
                    rows.Add(new JsonObject
                    {
                        ["id"] = f?["id"]?.GetValue<string>(),
                        ["datetime"] = props?["datetime"]?.GetValue<string>(),
                        ["cloud"] = props?["eo:cloud_cover"]?.DeepClone(),
                        ["mgrs"] = props?["grid:code"]?.GetValue<string>(),
                        ["geometry"] = f?["geometry"]?.DeepClone(),
                    });
                }

                progress.RecordsIn += rows.Count;
                await using (var db = await openDb())
                {
                    progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO satellite_scenes (id, collection, acquired_at, cloud_cover, mgrs_tile, footprint)
                        SELECT x->>'id', 'sentinel-2-l2a', (x->>'datetime')::timestamptz, (x->>'cloud')::real, x->>'mgrs',
                            ST_SetSRID(ST_GeomFromGeoJSON(x->>'geometry'), 4326)
                        FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS x WHERE x->>'id' IS NOT NULL
                        ON CONFLICT (id) DO UPDATE SET cloud_cover = EXCLUDED.cloud_cover
                        """, new { rows = rows.ToJsonString() }, commandTimeout: 120, cancellationToken: ct));
                }

                next = root["links"]?.AsArray().FirstOrDefault(l => l?["rel"]?.GetValue<string>() == "next")?["href"]?.GetValue<string>();
                var method = root["links"]?.AsArray().FirstOrDefault(l => l?["rel"]?.GetValue<string>() == "next")?["method"]?.GetValue<string>();
                if (method is "POST")
                {
                    // A POST-paged search would need the body again; the GET form is enough for this bounded query
                    progress.Warnings.Add("STAC next page uses POST; stopped paging");
                    next = null;
                }
            }
        }
    }
}

// GBIF presence-only records under open licences
public static class GbifConnector
{
    public static async Task IngestAsync(OpenDb openDb, string species, int taxonKey, (double West, double South, double East, double North) bbox, JobProgress progress, CancellationToken ct)
    {
        var wkt = string.Create(CultureInfo.InvariantCulture,
            $"POLYGON(({bbox.West} {bbox.South},{bbox.East} {bbox.South},{bbox.East} {bbox.North},{bbox.West} {bbox.North},{bbox.West} {bbox.South}))");

        for (var offset = 0; offset < 5000; offset += 300)
        {
            var url = $"https://api.gbif.org/v1/occurrence/search?taxonKey={taxonKey}&geometry={Uri.EscapeDataString(wkt)}&hasCoordinate=true&hasGeospatialIssue=false" +
                      $"&license=CC0_1_0&license=CC_BY_4_0&occurrenceStatus=PRESENT&limit=300&offset={offset}";
            var root = JsonNode.Parse(await Http.GetStringWithRetryAsync(url, ct))!;
            var results = root["results"]?.AsArray() ?? [];
            var rows = new JsonArray();

            foreach (var r in results)
            {
                rows.Add(new JsonObject
                {
                    ["id"] = r?["key"]?.DeepClone(),
                    ["date"] = r?["eventDate"]?.GetValue<string>() is { Length: >= 10 } d ? d[..10] : null,
                    ["unc"] = r?["coordinateUncertaintyInMeters"]?.DeepClone(),
                    ["license"] = r?["license"]?.GetValue<string>(),
                    ["dataset"] = r?["datasetKey"]?.GetValue<string>(),
                    ["basis"] = r?["basisOfRecord"]?.GetValue<string>(),
                    ["lat"] = r?["decimalLatitude"]?.DeepClone(),
                    ["lon"] = r?["decimalLongitude"]?.DeepClone(),
                });
            }

            progress.RecordsIn += rows.Count;
            await using (var db = await openDb())
            {
                progress.RecordsUpserted += await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO occurrence_records (gbif_id, species, event_date, coord_uncertainty_m, license, dataset_key, basis, geom)
                    SELECT (x->>'id')::bigint, @species, CASE WHEN x->>'date' ~ '^\d{4}-\d{2}-\d{2}$' THEN (x->>'date')::date END, (x->>'unc')::real,
                        x->>'license', x->>'dataset', x->>'basis', ST_SetSRID(ST_MakePoint((x->>'lon')::float8, (x->>'lat')::float8), 4326)
                    FROM jsonb_array_elements(CAST(@rows AS jsonb)) AS x WHERE x->>'lat' IS NOT NULL AND x->>'license' IS NOT NULL
                    ON CONFLICT (gbif_id) DO NOTHING
                    """, new { rows = rows.ToJsonString(), species }, cancellationToken: ct));
            }

            if (root["endOfRecords"]?.GetValue<bool>() != false)
            {
                break;
            }
        }
    }
}
