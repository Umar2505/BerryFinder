using System.Data.Common;
using Dapper;

// The data-source and licensing register, the supported species and the pilot region. Upserted on
// every start, so editing a row here and restarting updates the database.
public static class Registry
{
    public sealed record Source(string Id, string Name, string Provider, string Endpoint, string DocsUrl, string License, string Attribution,
        string CommercialUse, string Status, string Coverage, string Notes);

    public static readonly Source[] Sources =
    [
        new("metsakeskus_stands", "Forest stands (open forest resource data)", "Finnish Forest Centre (Suomen metsäkeskus)",
            "https://avoin.metsakeskus.fi/rajapinnat/v2/stand/ows", "https://www.metsakeskus.fi/fi/avoin-metsa-ja-luontotieto/aineistot-paikkatieto-ohjelmille/rajapinnat",
            "CC BY 4.0", "Suomen metsäkeskus, avoin metsävaratieto", "allowed_with_attribution", "active",
            "Privately owned forests across mainland Finland; state (Metsähallitus) land is largely not included",
            "WFS 2.0, GeoJSON in EPSG:3067. Site type, soil, development class, species mix, age, basal area, height, measurement dates."),
        new("gtk_soil", "Superficial deposits 1:20 000 / 1:50 000 / 1:200 000", "Geological Survey of Finland (GTK)",
            "https://gtkdata.gtk.fi/arcgis/services/Rajapinnat/GTK_Maapera_WFS/MapServer/WFSServer", "https://www.gtk.fi/en/services/data-sets-and-online-services-geo-fi/interface-services/",
            "CC BY 4.0", "Geologian tutkimuskeskus (GTK), maaperäkartta", "allowed_with_attribution", "active",
            "1:200 000 covers all of Finland; 1:20 000 and 1:50 000 only where mapped",
            "Surface and base deposit classes only. The service has no measured pH, nutrients or soil moisture; those stay missing."),
        new("fmi_weather", "Daily weather observations", "Finnish Meteorological Institute (FMI)",
            "https://opendata.fmi.fi/wfs", "https://en.ilmatieteenlaitos.fi/open-data",
            "CC BY 4.0", "Ilmatieteen laitos (FMI), avoin data", "allowed_with_attribution", "active",
            "Station observations near the pilot region",
            "Station data only. Values describe the station, not the forest; the app names the station and its distance wherever they are shown."),
        new("nasa_power", "Gridded daily weather and soil wetness (POWER)", "NASA Langley Research Center, POWER project (MERRA-2 / SYN1DEG)",
            "https://power.larc.nasa.gov/api/temporal/daily/point", "https://power.larc.nasa.gov/docs/services/api/",
            "NASA open data policy (no restrictions; acknowledgement requested)", "NASA Prediction Of Worldwide Energy Resources (POWER)", "allowed_with_attribution", "active",
            "Global grid at about 0.5° (roughly 55 km); one cell covers the Oulu pilot",
            "A reanalysis model grid, not an observation. Used as a cross-check and a fallback where an FMI station has gaps, and shown as 'modelled'."),
        new("copernicus_s2", "Sentinel-2 Level-2A scene catalogue", "Copernicus Data Space Ecosystem (ESA / EU)",
            "https://stac.dataspace.copernicus.eu/v1/", "https://documentation.dataspace.copernicus.eu/APIs/STAC.html",
            "Copernicus Sentinel data licence (free, full and open)", "Contains modified Copernicus Sentinel data", "allowed_with_attribution", "active",
            "Scene discovery for the pilot region",
            "Scene search runs in the app. Pixel download and NDVI/NDMI compositing run in the Python pipeline (ml/), which needs CDSE S3 credentials: pending."),
        new("osm_access", "Roads, forest tracks and paths", "OpenStreetMap contributors (via Overpass API)",
            "https://overpass-api.de/api/interpreter", "https://wiki.openstreetmap.org/wiki/Overpass_API",
            "ODbL 1.0", "© OpenStreetMap contributors", "allowed_with_attribution", "active",
            "Pilot region",
            "Share-alike applies to derived databases that are published. Distances to ways are straight-line and are labelled as such."),
        new("syke_protected", "Nature conservation areas", "Finnish Environment Institute (SYKE)",
            "https://paikkatiedot.ymparisto.fi/geoserver/inspire_ps/wfs", "https://www.syke.fi/en-US/Open_information",
            "CC BY 4.0", "Suomen ympäristökeskus (Syke)", "allowed_with_attribution", "active",
            "State-owned and private nature reserves and protected habitat types",
            "Used to warn about and, for strict reserves, exclude areas. Local rules still need checking on site."),
        new("gbif_occurrences", "Species occurrence records", "GBIF.org",
            "https://api.gbif.org/v1/occurrence/search", "https://www.gbif.org/developer/occurrence",
            "Per record: only CC0 1.0 and CC BY 4.0 records are ingested", "GBIF.org occurrence download, per-dataset citation", "allowed_with_attribution", "active",
            "Pilot region",
            "Presence-only. Used for a sanity check of the baseline, never as absence, abundance or yield."),
        new("nordic_habitat_model", "Nordic berry habitat model (PyTorch ensemble, 250 m grid)", "BerryFinder (notebooks/nordic_berry_habitat_model.ipynb)",
            "data/ml/berry_model_card.json", "https://github.com/Umar2505/BerryFinder/tree/main/notebooks",
            "Derived from CHELSA, SoilGrids, ESA WorldCover and Copernicus DEM (CC BY 4.0 or free use), Metsäkeskus (CC BY 4.0) and GBIF (CC0 / CC BY 4.0)",
            "CHELSA; ISRIC SoilGrids; ESA WorldCover; Copernicus DEM GLO-30; Suomen metsäkeskus; GBIF.org", "allowed_with_attribution", "active",
            "Oulu pilot square, scored offline at 250 m",
            "Trained on written habitat rules (pseudo-labels), checked against real GBIF sightings. Only species that beat chance by a clear margin get spots; odds stay capped below 'Promising'."),
        new("luke_berry_monitoring", "Wild berry yield monitoring and forecasts", "Natural Resources Institute Finland (Luke)",
            "https://www.luke.fi/fi/luonnonvaratieto/tiedetta-ja-tietoa/luonnonmarjat/luonnonmarjojen-satoseurannat-ja-ennusteet", "https://marjahavainnot.fi/",
            "Not established", "—", "pending_authorization", "pending_authorization",
            "Plot-level monitoring data across Finland",
            "No public download API or licence for commercial ML use was found. Adapter is disabled until Luke grants access; a data request is the next step."),
        new("nls_terrain", "Elevation model and orthophotos", "National Land Survey of Finland (MML)",
            "https://avoin-paikkatieto.maanmittauslaitos.fi/", "https://www.maanmittauslaitos.fi/en/rajapinnat/api-avaimen-ohje",
            "CC BY 4.0", "Maanmittauslaitos", "allowed_with_attribution", "pending_credentials",
            "All of Finland",
            "Needs a free MML API key (secret NLS_API_KEY). Without it the satellite basemap is disabled and terrain features are missing."),
        new("field_observations", "BerryFinder field observations", "BerryFinder users and pilot surveyors",
            "In-app", "In-app protocol", "Contributor consent per observation", "BerryFinder", "allowed", "active",
            "Wherever users submit",
            "Private by default. Used for training only with the contributor's explicit consent."),
    ];

    public sealed record SpeciesRow(string Id, string Scientific, string NameEn, string NameFi, bool Supported, int? GbifTaxon, string Notes);

    public static readonly SpeciesRow[] SpeciesRows =
    [
        new(Species.Bilberry, "Vaccinium myrtillus", "Bilberry", "Mustikka", true, 2882833, "Mesic heath forests; recovers slowly after clear-cutting"),
        new(Species.Lingonberry, "Vaccinium vitis-idaea", "Lingonberry", "Puolukka", true, 2882835, "Sub-xeric pine heaths; tolerates more open canopy"),
        // Not Supported: no forest-stand baseline. Their spots, where shown, come from the Nordic grid model (data/ml)
        new(Species.Cloudberry, "Rubus chamaemorus", "Cloudberry", "Hilla", false, 2998290, "Open mires; grid model only, no stand baseline"),
        new("raspberry", "Rubus idaeus", "Raspberry", "Vadelma", false, 2993094, "Clearings and edges; grid model only"),
        new("bog_bilberry", "Vaccinium uliginosum", "Bog bilberry", "Juolukka", false, 8073364, "Mires and fell heaths; grid model only"),
        new("cranberry", "Vaccinium oxycoccos", "Cranberry", "Karpalo", false, 2882940, "Open bogs; grid model only"),
        new("crowberry", "Empetrum nigrum", "Crowberry", "Variksenmarja", false, 8925249, "Dry heaths and bogs; grid model only"),
        new("wild_strawberry", "Fragaria vesca", "Wild strawberry", "Metsämansikka", false, 3029817, "Sunny edges and meadows; grid model only"),
        new("arctic_bramble", "Rubus arcticus", "Arctic bramble", "Mesimarja", false, 2992051, "Moist meadows and mire edges; grid model only"),
        new("sea_buckthorn", "Hippophae rhamnoides", "Sea buckthorn", "Tyrni", false, 3039285, "Sandy sea shores; grid model only"),
    ];

    // Oulu pilot: 25 km x 25 km south-east of the city centre, in 5 km tiles (EPSG:3067)
    public const string PilotRegion = "oulu";
    public const int PilotXMin = 420000, PilotYMin = 7190000, PilotXMax = 445000, PilotYMax = 7215000, TileSize = 5000;

    public static async Task EnsureAsync(DbConnection db)
    {
        foreach (var s in Sources)
        {
            // Status is left alone on update once a run has changed it, except for sources whose status is decided here
            await db.ExecuteAsync("""
                INSERT INTO data_sources (id, name, provider, endpoint, docs_url, license, attribution, commercial_use, status, coverage, notes)
                VALUES (@Id, @Name, @Provider, @Endpoint, @DocsUrl, @License, @Attribution, @CommercialUse, @Status, @Coverage, @Notes)
                ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, provider = EXCLUDED.provider, endpoint = EXCLUDED.endpoint, docs_url = EXCLUDED.docs_url,
                    license = EXCLUDED.license, attribution = EXCLUDED.attribution, commercial_use = EXCLUDED.commercial_use, coverage = EXCLUDED.coverage,
                    notes = EXCLUDED.notes, status = CASE WHEN data_sources.status IN ('active', 'pending_credentials', 'pending_authorization', 'planned') THEN EXCLUDED.status ELSE data_sources.status END,
                    updated_at = now()
                """, s);
        }

        foreach (var sp in SpeciesRows)
        {
            await db.ExecuteAsync("""
                INSERT INTO berry_species (id, scientific, name_en, name_fi, supported, gbif_taxon, notes)
                VALUES (@Id, @Scientific, @NameEn, @NameFi, @Supported, @GbifTaxon, @Notes)
                ON CONFLICT (id) DO UPDATE SET scientific = EXCLUDED.scientific, name_en = EXCLUDED.name_en, name_fi = EXCLUDED.name_fi,
                    supported = EXCLUDED.supported, gbif_taxon = EXCLUDED.gbif_taxon, notes = EXCLUDED.notes
                """, sp);
        }

        for (var x = PilotXMin; x < PilotXMax; x += TileSize)
        {
            for (var y = PilotYMin; y < PilotYMax; y += TileSize)
            {
                await db.ExecuteAsync("""
                    INSERT INTO pilot_tiles (id, region, x_min, y_min, x_max, y_max, geom)
                    VALUES (@id, @region, @x, @y, @x2, @y2, ST_MakeEnvelope(@x, @y, @x2, @y2, 3067))
                    ON CONFLICT (id) DO NOTHING
                    """, new { id = TileId(x, y), region = PilotRegion, x, y, x2 = x + TileSize, y2 = y + TileSize });
            }
        }
    }

    public static string TileId(int x, int y) => $"E{x / 1000}N{y / 1000}";
}
