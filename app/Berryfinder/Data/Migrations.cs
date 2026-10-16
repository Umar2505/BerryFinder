using System.Data.Common;
using Dapper;

// Versioned, forward-only schema migrations for the app's PostGIS database. Each entry runs once,
// inside a transaction, and is recorded in schema_migrations. Never edit an applied migration:
// append a new one. A Postgres advisory lock keeps two instances from migrating at the same time.
public static class Migrations
{
    public sealed record Migration(int Version, string Name, string Sql);

    public static readonly Migration[] All =
    [
        new(1, "core_schema", """
            CREATE EXTENSION IF NOT EXISTS postgis;

            -- Provenance register: every connector writes its licence, coverage and run history here
            CREATE TABLE IF NOT EXISTS data_sources (
                id               text PRIMARY KEY,
                name             text NOT NULL,
                provider         text NOT NULL,
                endpoint         text,
                docs_url         text,
                license          text NOT NULL,
                attribution      text NOT NULL,
                commercial_use   text NOT NULL,          -- allowed | allowed_with_attribution | verify | pending_authorization
                status           text NOT NULL,          -- active | pending_credentials | pending_authorization | planned
                coverage         text,
                notes            text,
                schema_version   text,
                records          bigint NOT NULL DEFAULT 0,
                last_attempt_at  timestamptz,
                last_success_at  timestamptz,
                updated_at       timestamptz NOT NULL DEFAULT now()
            );

            -- The pilot region is a set of 5 km x 5 km tiles in EPSG:3067; ingestion runs per tile so
            -- the region grows without re-downloading what is already stored
            CREATE TABLE IF NOT EXISTS pilot_tiles (
                id                     text PRIMARY KEY,
                region                 text NOT NULL,
                x_min                  int NOT NULL,
                y_min                  int NOT NULL,
                x_max                  int NOT NULL,
                y_max                  int NOT NULL,
                enabled                boolean NOT NULL DEFAULT true,
                geom                   geometry(Polygon, 3067) NOT NULL,
                stand_count            int,
                stands_ingested_at     timestamptz,
                soil_ingested_at       timestamptz,
                access_ingested_at     timestamptz,
                protected_ingested_at  timestamptz,
                features_built_at      timestamptz
            );
            CREATE INDEX IF NOT EXISTS pilot_tiles_geom_idx ON pilot_tiles USING gist (geom);

            CREATE TABLE IF NOT EXISTS ingestion_jobs (
                id                bigserial PRIMARY KEY,
                source_id         text NOT NULL REFERENCES data_sources(id),
                kind              text NOT NULL,
                params            jsonb NOT NULL DEFAULT '{}'::jsonb,
                status            text NOT NULL DEFAULT 'queued',   -- queued | running | succeeded | failed | cancelled
                requested_by      text,
                created_at        timestamptz NOT NULL DEFAULT now(),
                started_at        timestamptz,
                finished_at       timestamptz,
                progress          real NOT NULL DEFAULT 0,
                records_in        int NOT NULL DEFAULT 0,
                records_upserted  int NOT NULL DEFAULT 0,
                message           text,
                error             text
            );
            CREATE INDEX IF NOT EXISTS ingestion_jobs_status_idx ON ingestion_jobs (status, created_at DESC);

            -- Finnish Forest Centre stands, stored in their native EPSG:3067 with a web-mercator copy for tiles
            CREATE TABLE IF NOT EXISTS forest_stands (
                stand_id               bigint PRIMARY KEY,
                municipality           text,
                land_class             text,          -- maingroup: 1 forest land, 2 poorly productive, 3 unproductive
                site_main_class        text,          -- subgroup: 1 mineral soil, 2 spruce mire, 3 pine mire, 4 open mire
                fertility_class        smallint,
                soil_type              smallint,
                drainage_state         text,
                development_class      text,
                main_species           smallint,
                prop_pine              real,
                prop_spruce            real,
                prop_other             real,
                mean_age               real,
                basal_area             real,
                stem_count             int,
                mean_diameter          real,
                mean_height            real,
                volume                 real,
                cutting_type           text,
                cutting_proposal_year  int,
                data_source_code       text,
                tree_data_date         date,
                measurement_date       date,
                source_created_at      timestamptz,
                source_updated_at      timestamptz,
                area_ha                real,
                geom                   geometry(MultiPolygon, 3067) NOT NULL,
                geom_web               geometry(MultiPolygon, 3857) NOT NULL,
                centroid               geometry(Point, 4326) NOT NULL,
                tile_id                text REFERENCES pilot_tiles(id),
                ingested_at            timestamptz NOT NULL DEFAULT now(),
                schema_version         text NOT NULL
            );
            CREATE INDEX IF NOT EXISTS forest_stands_geom_idx ON forest_stands USING gist (geom);
            CREATE INDEX IF NOT EXISTS forest_stands_geom_web_idx ON forest_stands USING gist (geom_web);
            CREATE INDEX IF NOT EXISTS forest_stands_centroid_idx ON forest_stands USING gist (centroid);
            CREATE INDEX IF NOT EXISTS forest_stands_tile_idx ON forest_stands (tile_id);

            -- GTK superficial deposit polygons (several map scales; the finest available wins)
            CREATE TABLE IF NOT EXISTS soil_polygons (
                id                bigserial PRIMARY KEY,
                source_layer      text NOT NULL,
                scale_rank        smallint NOT NULL,      -- 1 = finest (1:20k) … 4 = coarsest (1:200k)
                source_object_id  bigint NOT NULL,
                surface_code      bigint,
                surface_label     text,
                base_label        text,
                soil_class        text NOT NULL,
                geom              geometry(MultiPolygon, 3067) NOT NULL,
                ingested_at       timestamptz NOT NULL DEFAULT now(),
                UNIQUE (source_layer, source_object_id)
            );
            CREATE INDEX IF NOT EXISTS soil_polygons_geom_idx ON soil_polygons USING gist (geom);

            -- Per-stand soil join result
            CREATE TABLE IF NOT EXISTS soil_features (
                stand_id      bigint PRIMARY KEY REFERENCES forest_stands(stand_id) ON DELETE CASCADE,
                soil_class    text NOT NULL,
                soil_label    text,
                source_layer  text,
                updated_at    timestamptz NOT NULL DEFAULT now()
            );

            -- OpenStreetMap ways that give access: roads, forest tracks and paths
            CREATE TABLE IF NOT EXISTS access_ways (
                osm_id       bigint PRIMARY KEY,
                kind         text NOT NULL,          -- road | track | path
                highway      text NOT NULL,
                access_tag   text,
                name         text,
                geom         geometry(LineString, 3067) NOT NULL,
                ingested_at  timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS access_ways_geom_idx ON access_ways USING gist (geom);
            CREATE INDEX IF NOT EXISTS access_ways_kind_idx ON access_ways (kind);

            -- Protected areas from SYKE and restrictions added by admins
            CREATE TABLE IF NOT EXISTS access_restrictions (
                id                bigserial PRIMARY KEY,
                source            text NOT NULL,          -- syke | admin
                source_ref        text NOT NULL,
                name              text,
                restriction_type  text NOT NULL,
                severity          text NOT NULL,          -- warn | exclude
                legal_basis       text,
                notes             text,
                active            boolean NOT NULL DEFAULT true,
                geom              geometry(MultiPolygon, 3067) NOT NULL,
                created_by        text,
                created_at        timestamptz NOT NULL DEFAULT now(),
                UNIQUE (source, source_ref)
            );
            CREATE INDEX IF NOT EXISTS access_restrictions_geom_idx ON access_restrictions USING gist (geom);

            -- Per-stand access join result. Distances are straight-line from the nearest way and say so.
            CREATE TABLE IF NOT EXISTS access_features (
                stand_id             bigint PRIMARY KEY REFERENCES forest_stands(stand_id) ON DELETE CASCADE,
                dist_road_m          real,
                dist_track_m         real,
                dist_path_m          real,
                access_point         geometry(Point, 4326),
                access_kind          text,
                restriction_id       bigint REFERENCES access_restrictions(id) ON DELETE SET NULL,
                restriction_severity text,
                updated_at           timestamptz NOT NULL DEFAULT now()
            );

            -- Sentinel-2 L2A scenes discovered through the Copernicus STAC API
            CREATE TABLE IF NOT EXISTS satellite_scenes (
                id             text PRIMARY KEY,
                collection     text NOT NULL,
                acquired_at    timestamptz NOT NULL,
                cloud_cover    real,
                mgrs_tile      text,
                footprint      geometry(Geometry, 4326),
                processed      boolean NOT NULL DEFAULT false,
                discovered_at  timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS satellite_scenes_time_idx ON satellite_scenes (acquired_at);

            -- Seasonal spectral composites per stand, written by the Python raster pipeline (ml/)
            CREATE TABLE IF NOT EXISTS satellite_features (
                stand_id      bigint NOT NULL REFERENCES forest_stands(stand_id) ON DELETE CASCADE,
                period        text NOT NULL,
                ndvi_median   real,
                ndmi_median   real,
                valid_pixels  int,
                scene_count   int,
                processed_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (stand_id, period)
            );

            CREATE TABLE IF NOT EXISTS weather_stations (
                fmisid     int PRIMARY KEY,
                name       text NOT NULL,
                geom       geometry(Point, 4326) NOT NULL,
                added_at   timestamptz NOT NULL DEFAULT now()
            );

            -- FMI station observations (never interpolated or presented as on-site)
            CREATE TABLE IF NOT EXISTS weather_daily (
                fmisid      int NOT NULL REFERENCES weather_stations(fmisid),
                day         date NOT NULL,
                t_mean      real,
                t_min       real,
                t_max       real,
                precip_mm   real,
                snow_cm     real,
                ground_min  real,
                fetched_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (fmisid, day)
            );

            CREATE TABLE IF NOT EXISTS berry_species (
                id            text PRIMARY KEY,
                scientific    text NOT NULL,
                name_en       text NOT NULL,
                name_fi       text NOT NULL,
                supported     boolean NOT NULL,
                gbif_taxon    int,
                notes         text
            );

            -- Model registry: heuristic baselines and trained models share one table
            CREATE TABLE IF NOT EXISTS model_versions (
                id             bigserial PRIMARY KEY,
                species        text NOT NULL REFERENCES berry_species(id),
                layer          text NOT NULL,            -- habitat | season
                kind           text NOT NULL,            -- heuristic | logistic | random_forest
                version        text NOT NULL,
                status         text NOT NULL,            -- candidate | production | retired | rejected | insufficient_data
                params         jsonb NOT NULL DEFAULT '{}'::jsonb,
                metrics        jsonb NOT NULL DEFAULT '{}'::jsonb,
                training_rows  int NOT NULL DEFAULT 0,
                notes          text,
                created_by     text,
                created_at     timestamptz NOT NULL DEFAULT now(),
                promoted_at    timestamptz,
                promoted_by    text,
                UNIQUE (species, layer, version)
            );

            CREATE TABLE IF NOT EXISTS predictions (
                stand_id          bigint NOT NULL REFERENCES forest_stands(stand_id) ON DELETE CASCADE,
                species           text NOT NULL REFERENCES berry_species(id),
                model_version_id  bigint NOT NULL REFERENCES model_versions(id) ON DELETE CASCADE,
                prediction_kind   text NOT NULL,          -- heuristic | trained
                score             real,                   -- internal ranking index, never shown as a probability
                probability       real,                   -- only for a calibrated trained model
                suitability       text NOT NULL,          -- high | moderate | low | unlikely | not_assessed
                evidence          text NOT NULL,          -- good | fair | limited
                drivers           jsonb NOT NULL DEFAULT '[]'::jsonb,
                excluded          boolean NOT NULL DEFAULT false,
                computed_at       timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (stand_id, species, model_version_id)
            );
            CREATE INDEX IF NOT EXISTS predictions_model_idx ON predictions (model_version_id, species, suitability);

            -- Contiguous good stands merged into named picking areas, per species and model version
            CREATE TABLE IF NOT EXISTS picking_areas (
                id                bigserial PRIMARY KEY,
                species           text NOT NULL REFERENCES berry_species(id),
                model_version_id  bigint NOT NULL REFERENCES model_versions(id) ON DELETE CASCADE,
                suitability       text NOT NULL,
                mean_score        real NOT NULL,
                area_ha           real NOT NULL,
                stand_count       int NOT NULL,
                high_share        real NOT NULL,
                dist_road_m       real,
                access_point      geometry(Point, 4326),
                restriction       text,
                geom              geometry(MultiPolygon, 3067) NOT NULL,
                geom_web          geometry(MultiPolygon, 3857) NOT NULL,
                centroid          geometry(Point, 4326) NOT NULL,
                published_at      timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS picking_areas_geom_web_idx ON picking_areas USING gist (geom_web);
            CREATE INDEX IF NOT EXISTS picking_areas_centroid_idx ON picking_areas USING gist (centroid);
            CREATE INDEX IF NOT EXISTS picking_areas_model_idx ON picking_areas (model_version_id, species);

            CREATE TABLE IF NOT EXISTS user_accounts (
                user_id                   text PRIMARY KEY,
                role                      text NOT NULL DEFAULT 'consumer',   -- consumer | professional | admin
                language                  text,
                consent_share_aggregated  boolean NOT NULL DEFAULT false,
                consent_training          boolean NOT NULL DEFAULT false,
                consent_updated_at        timestamptz,
                created_at                timestamptz NOT NULL DEFAULT now(),
                last_seen_at              timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS subscriptions (
                id            bigserial PRIMARY KEY,
                user_id       text NOT NULL REFERENCES user_accounts(user_id) ON DELETE CASCADE,
                offer_id      text NOT NULL,
                status        text NOT NULL,
                provider_ref  text,
                started_at    timestamptz NOT NULL DEFAULT now(),
                ended_at      timestamptz
            );

            CREATE TABLE IF NOT EXISTS saved_locations (
                id          bigserial PRIMARY KEY,
                user_id     text NOT NULL REFERENCES user_accounts(user_id) ON DELETE CASCADE,
                label       text NOT NULL,
                species     text REFERENCES berry_species(id),
                area_id     bigint,
                stand_id    bigint,
                geom        geometry(Point, 4326) NOT NULL,
                notes       text,
                created_at  timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS saved_locations_user_idx ON saved_locations (user_id);

            CREATE TABLE IF NOT EXISTS picking_trips (
                id          bigserial PRIMARY KEY,
                user_id     text REFERENCES user_accounts(user_id) ON DELETE CASCADE,
                start_geom  geometry(Point, 4326) NOT NULL,
                params      jsonb NOT NULL,
                candidates  jsonb NOT NULL,
                created_at  timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS field_observations (
                id                bigserial PRIMARY KEY,
                user_id           text NOT NULL REFERENCES user_accounts(user_id) ON DELETE CASCADE,
                species           text NOT NULL REFERENCES berry_species(id),
                geom              geometry(Point, 4326) NOT NULL,
                gps_accuracy_m    real,
                location_source   text NOT NULL,          -- gps | map_pick | area_centroid
                observed_on       date NOT NULL,
                submitted_at      timestamptz NOT NULL DEFAULT now(),
                method            text NOT NULL,          -- visit | plot_1m2
                outcome           text NOT NULL,          -- none | few | moderate | many | unripe | not_surveyed
                stage             text,                   -- flowering | unripe | ripe | overripe
                effort_minutes    int,
                area_searched_m2  real,
                amount_litres     real,
                plot_count        int,
                ripe_count        int,
                unripe_count      int,
                flower_count      int,
                stand_id          bigint,
                area_id           bigint,
                share_aggregated  boolean NOT NULL DEFAULT false,
                use_for_training  boolean NOT NULL DEFAULT false,
                qc_status         text NOT NULL DEFAULT 'pending',   -- pending | accepted | rejected | needs_info
                qc_flags          jsonb NOT NULL DEFAULT '[]'::jsonb,
                qc_note           text,
                qc_by             text,
                qc_at             timestamptz,
                duplicate_of      bigint REFERENCES field_observations(id) ON DELETE SET NULL,
                notes             text,
                is_synthetic      boolean NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS field_observations_geom_idx ON field_observations USING gist (geom);
            CREATE INDEX IF NOT EXISTS field_observations_user_idx ON field_observations (user_id, submitted_at DESC);
            CREATE INDEX IF NOT EXISTS field_observations_qc_idx ON field_observations (qc_status, submitted_at);

            CREATE TABLE IF NOT EXISTS observation_media (
                id              bigserial PRIMARY KEY,
                observation_id  bigint NOT NULL REFERENCES field_observations(id) ON DELETE CASCADE,
                asset_uri       text NOT NULL,
                mime_type       text NOT NULL,
                width           int,
                height          int,
                bytes           bigint,
                quality_flags   jsonb NOT NULL DEFAULT '[]'::jsonb,
                created_at      timestamptz NOT NULL DEFAULT now()
            );

            -- GBIF presence-only records. Never used as absence, abundance or yield.
            CREATE TABLE IF NOT EXISTS occurrence_records (
                gbif_id        bigint PRIMARY KEY,
                species        text NOT NULL REFERENCES berry_species(id),
                event_date     date,
                coord_uncertainty_m real,
                license        text NOT NULL,
                dataset_key    text,
                basis          text,
                geom           geometry(Point, 4326) NOT NULL,
                ingested_at    timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS occurrence_records_geom_idx ON occurrence_records USING gist (geom);

            CREATE TABLE IF NOT EXISTS audit_logs (
                id       bigserial PRIMARY KEY,
                at       timestamptz NOT NULL DEFAULT now(),
                actor    text,
                action   text NOT NULL,
                target   text,
                details  jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE INDEX IF NOT EXISTS audit_logs_at_idx ON audit_logs (at DESC);

            -- Product analytics: event names and coarse properties only, never typed text or coordinates
            CREATE TABLE IF NOT EXISTS analytics_events (
                id       bigserial PRIMARY KEY,
                at       timestamptz NOT NULL DEFAULT now(),
                user_id  text,
                event    text NOT NULL,
                props    jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE INDEX IF NOT EXISTS analytics_events_event_idx ON analytics_events (event, at DESC);

            CREATE TABLE IF NOT EXISTS contact_messages (
                id        bigserial PRIMARY KEY,
                at        timestamptz NOT NULL DEFAULT now(),
                user_id   text,
                email     text NOT NULL,
                topic     text NOT NULL,
                message   text NOT NULL,
                status    text NOT NULL DEFAULT 'new'
            );

            CREATE TABLE IF NOT EXISTS app_owner (
                id          int PRIMARY KEY CHECK (id = 1),
                user_id     text NOT NULL,
                claimed_at  timestamptz NOT NULL DEFAULT now()
            );
            """),
        new(2, "nasa_power_grid", """
            -- NASA POWER gridded (modelled) daily weather. Kept apart from FMI station observations: a grid-cell value is never a station reading.
            CREATE TABLE IF NOT EXISTS weather_grid_daily (
                grid_id       text NOT NULL,
                cell_lat      real NOT NULL,
                cell_lon      real NOT NULL,
                day           date NOT NULL,
                t_mean        real,
                t_min         real,
                precip_mm     real,
                root_wetness  real,      -- GWETROOT: modelled root-zone soil wetness, 0 (dry) to 1 (saturated)
                solar_mj      real,      -- ALLSKY_SFC_SW_DWN, MJ/m2/day
                fetched_at    timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (grid_id, day)
            );
            CREATE INDEX IF NOT EXISTS weather_grid_daily_day_idx ON weather_grid_daily (day);
            """),
    ];

    public static async Task<int> ApplyAsync(DbConnection connection, CancellationToken ct = default)
    {
        await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())");

        // Session-level advisory lock: concurrent instances wait here instead of racing the DDL
        await connection.ExecuteAsync("SELECT pg_advisory_lock(424242)");

        try
        {
            var applied = (await connection.QueryAsync<int>("SELECT version FROM schema_migrations")).ToHashSet();
            var count = 0;

            foreach (var migration in All.OrderBy(m => m.Version).Where(m => !applied.Contains(m.Version)))
            {
                ct.ThrowIfCancellationRequested();
                await using var tx = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: tx, commandTimeout: 300, cancellationToken: ct));
                await connection.ExecuteAsync("INSERT INTO schema_migrations (version, name) VALUES (@Version, @Name)", new { migration.Version, migration.Name }, tx);
                await tx.CommitAsync(ct);
                count++;
            }

            return count;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(424242)");
        }
    }
}
