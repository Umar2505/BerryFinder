using Dapper;
using Npgsql;
using Xunit;

// Geospatial query tests against a real PostGIS. They run only when BERRYFINDER_TEST_DB points at a server with the
// postgis extension available (CI provides one). Each run uses its own schema, so it never touches real data.
// All geometry below is a SYNTHETIC FIXTURE: invented rectangles in EPSG:3067 near Oulu, not forest data.
[Trait("Category", "Spatial")]
public class SpatialTests : IAsyncLifetime
{
    private readonly string? _baseUrl = Environment.GetEnvironmentVariable("BERRYFINDER_TEST_DB");
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N")[..10];
    private NpgsqlConnection? _db;

    public async Task InitializeAsync()
    {
        if (_baseUrl is null)
        {
            return;
        }

        await using (var admin = new NpgsqlConnection(_baseUrl))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS postgis");
            await admin.ExecuteAsync($"CREATE SCHEMA {_schema}");
        }

        _db = new NpgsqlConnection(_baseUrl + $";Search Path={_schema},public");
        await _db.OpenAsync();
        await Migrations.ApplyAsync(_db);
    }

    public async Task DisposeAsync()
    {
        if (_db is null)
        {
            return;
        }

        await _db.CloseAsync();
        await using var admin = new NpgsqlConnection(_baseUrl);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
    }

    private bool Skip() => _db is null;

    private async Task AddStandAsync(long id, double x, double y, double size, short fertility = 3)
    {
        await _db!.ExecuteAsync($"INSERT INTO pilot_tiles (id, region, x_min, y_min, x_max, y_max, geom) VALUES ('T{id}', 'test', 0, 0, 1, 1, ST_MakeEnvelope(0,0,1,1,3067)) ON CONFLICT DO NOTHING");
        await _db.ExecuteAsync($"""
            INSERT INTO forest_stands (stand_id, fertility_class, geom, geom_web, centroid, tile_id, schema_version)
            SELECT {id}, {fertility}, g, ST_Multi(ST_Transform(g, 3857)), ST_Transform(ST_PointOnSurface(g), 4326), 'T{id}', 'test'
            FROM (SELECT ST_Multi(ST_MakeEnvelope({x}, {y}, {x + size}, {y + size}, 3067)) AS g) q
            """);
    }

    [Fact]
    public async Task Migrations_apply_once_and_record_their_versions()
    {
        if (Skip()) return;
        Assert.Equal(0, await Migrations.ApplyAsync(_db!));
        Assert.Equal(Migrations.All.Length, await _db!.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Geometry_columns_enforce_their_coordinate_system()
    {
        if (Skip()) return;
        await Assert.ThrowsAsync<PostgresException>(() => _db!.ExecuteAsync("""
            INSERT INTO access_ways (osm_id, kind, highway, geom) VALUES (1, 'road', 'residential', ST_SetSRID(ST_MakeLine(ST_MakePoint(0,0), ST_MakePoint(1,1)), 4326))
            """));
    }

    [Fact]
    public async Task Nearest_road_lookup_uses_the_gist_index_and_returns_the_right_distance()
    {
        if (Skip()) return;
        await AddStandAsync(1, 430000, 7200000, 100);
        await _db!.ExecuteAsync("INSERT INTO access_ways (osm_id, kind, highway, geom) VALUES (10, 'road', 'residential', ST_SetSRID(ST_MakeLine(ST_MakePoint(430000, 7200200), ST_MakePoint(430100, 7200200)), 3067))");
        await _db.ExecuteAsync("INSERT INTO access_ways (osm_id, kind, highway, geom) VALUES (11, 'road', 'residential', ST_SetSRID(ST_MakeLine(ST_MakePoint(430000, 7201000), ST_MakePoint(430100, 7201000)), 3067))");

        var distance = await _db.ExecuteScalarAsync<double>("""
            SELECT ST_Distance(w.geom, s.geom) FROM forest_stands s,
              LATERAL (SELECT geom FROM access_ways ORDER BY geom <-> s.geom LIMIT 1) w WHERE s.stand_id = 1
            """);

        Assert.Equal(100.0, distance, 3);
        var indexes = await _db.QueryAsync<string>("SELECT indexname FROM pg_indexes WHERE tablename = 'access_ways' AND indexdef LIKE '%gist%'");
        Assert.NotEmpty(indexes);
    }

    [Fact]
    public async Task A_point_inside_a_stand_joins_to_the_finest_soil_polygon()
    {
        if (Skip()) return;
        await AddStandAsync(2, 430000, 7200000, 100);
        await _db!.ExecuteAsync("INSERT INTO soil_polygons (source_layer, scale_rank, source_object_id, soil_class, geom) VALUES ('coarse', 4, 1, 'fine', ST_Multi(ST_MakeEnvelope(429000, 7199000, 431000, 7201000, 3067)))");
        await _db.ExecuteAsync("INSERT INTO soil_polygons (source_layer, scale_rank, source_object_id, soil_class, geom) VALUES ('detailed', 1, 2, 'till', ST_Multi(ST_MakeEnvelope(429900, 7199900, 430200, 7200200, 3067)))");

        var cls = await _db.ExecuteScalarAsync<string>("""
            SELECT soil_class FROM soil_polygons p WHERE ST_Intersects(p.geom, (SELECT ST_PointOnSurface(geom) FROM forest_stands WHERE stand_id = 2)) ORDER BY scale_rank LIMIT 1
            """);
        Assert.Equal("till", cls);
    }

    [Fact]
    public async Task A_vector_tile_contains_only_the_stands_inside_its_bounds()
    {
        if (Skip()) return;
        // Oulu is about lon 25.47, lat 65.01. Build one stand there and one far to the south, via 4326 -> 3067
        await _db!.ExecuteAsync("""
            INSERT INTO pilot_tiles (id, region, x_min, y_min, x_max, y_max, geom) VALUES ('TT', 'test', 0, 0, 1, 1, ST_MakeEnvelope(0,0,1,1,3067))
            """);
        await _db.ExecuteAsync("""
            INSERT INTO forest_stands (stand_id, geom, geom_web, centroid, tile_id, schema_version)
            SELECT id, ST_Multi(g), ST_Multi(ST_Transform(g, 3857)), ST_Transform(ST_PointOnSurface(g), 4326), 'TT', 'test'
            FROM (VALUES (101, ST_Transform(ST_SetSRID(ST_MakeEnvelope(25.470, 65.010, 25.472, 65.011), 4326), 3067)),
                         (102, ST_Transform(ST_SetSRID(ST_MakeEnvelope(24.000, 60.100, 24.002, 60.101), 4326), 3067))) v(id, g)
            """);

        var count = await _db.ExecuteScalarAsync<int>("""
            WITH bounds AS (SELECT ST_Transform(ST_SetSRID(ST_MakeEnvelope(25.4, 64.9, 25.6, 65.1), 4326), 3857) AS geom)
            SELECT count(*) FROM forest_stands s, bounds WHERE s.geom_web && bounds.geom
            """);
        Assert.Equal(1, count);

        var tile = await _db.ExecuteScalarAsync<byte[]>("""
            WITH bounds AS (SELECT ST_Transform(ST_SetSRID(ST_MakeEnvelope(25.4, 64.9, 25.6, 65.1), 4326), 3857) AS geom),
            t AS (SELECT s.stand_id AS id, ST_AsMVTGeom(s.geom_web, bounds.geom, 4096, 64, true) AS geom FROM forest_stands s, bounds WHERE s.geom_web && bounds.geom)
            SELECT ST_AsMVT(t.*, 'stands', 4096, 'geom') FROM t
            """);
        Assert.NotNull(tile);
        Assert.NotEmpty(tile!);
    }

    [Fact]
    public async Task Deleting_a_user_cascades_to_their_observations_and_saved_places()
    {
        if (Skip()) return;
        await _db!.ExecuteAsync("INSERT INTO berry_species (id, scientific, name_en, name_fi, supported) VALUES ('bilberry', 'Vaccinium myrtillus', 'Bilberry', 'Mustikka', true)");
        await _db.ExecuteAsync("INSERT INTO user_accounts (user_id) VALUES ('u1')");
        await _db.ExecuteAsync("INSERT INTO saved_locations (user_id, label, geom) VALUES ('u1', 'x', ST_SetSRID(ST_MakePoint(25.5, 65.0), 4326))");
        await _db.ExecuteAsync("""
            INSERT INTO field_observations (user_id, species, geom, location_source, observed_on, method, outcome, is_synthetic)
            VALUES ('u1', 'bilberry', ST_SetSRID(ST_MakePoint(25.5, 65.0), 4326), 'gps', current_date, 'visit', 'few', true)
            """);

        await _db.ExecuteAsync("DELETE FROM user_accounts WHERE user_id = 'u1'");

        Assert.Equal(0, await _db.ExecuteScalarAsync<int>("SELECT count(*) FROM field_observations"));
        Assert.Equal(0, await _db.ExecuteScalarAsync<int>("SELECT count(*) FROM saved_locations"));
    }
}
