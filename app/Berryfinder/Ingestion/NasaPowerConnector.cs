using System.Globalization;
using Dapper;

// NASA POWER daily data (MERRA-2 reanalysis and SYN1DEG satellite-derived solar), a gridded, modelled
// product at roughly 0.5° resolution. It is stored apart from FMI's station observations and always
// labelled as a model grid: a value describes the whole grid cell, never the forest being viewed.
// API: https://power.larc.nasa.gov/docs/services/api/  (no key; cite "NASA POWER")
public static class NasaPowerConnector
{
    private const string Endpoint = "https://power.larc.nasa.gov/api/temporal/daily/point";
    private const string Parameters = "T2M,T2M_MIN,PRECTOTCORR,GWETROOT,ALLSKY_SFC_SW_DWN";

    public static async Task IngestAsync(OpenDb openDb, double lat, double lon, int years, JobProgress progress, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var gridId = NasaPowerParser.GridId(lat, lon);

        for (var year = today.Year - years; year <= today.Year; year++)
        {
            var from = new DateOnly(year, 3, 25);
            // POWER lags real time by a few days; asking past its latest day returns an error, so stop short of today
            var to = year == today.Year ? today.AddDays(-3) : new DateOnly(year, 11, 15);

            if (to <= from)
            {
                continue;
            }

            await using (var check = await openDb())
            {
                var stored = await check.ExecuteScalarAsync<int>("SELECT count(*) FROM weather_grid_daily WHERE grid_id = @gridId AND day BETWEEN @from AND @to", new { gridId, from, to });
                if (year < today.Year && stored >= (to.DayNumber - from.DayNumber) - 3)
                {
                    continue;
                }
            }

            var url = string.Create(CultureInfo.InvariantCulture,
                $"{Endpoint}?parameters={Parameters}&community=AG&longitude={lon:0.####}&latitude={lat:0.####}&start={from:yyyyMMdd}&end={to:yyyyMMdd}&format=JSON");
            var days = NasaPowerParser.Parse(await Http.GetStringWithRetryAsync(url, ct));

            if (days.Count == 0)
            {
                progress.Warnings.Add($"NASA POWER returned no data for {year}");
                continue;
            }

            progress.RecordsIn += days.Count;
            var (cellLat, cellLon) = NasaPowerParser.CellOf(lat, lon);
            await using var db = await openDb();
            progress.RecordsUpserted += await db.ExecuteAsync("""
                INSERT INTO weather_grid_daily (grid_id, cell_lat, cell_lon, day, t_mean, t_min, precip_mm, root_wetness, solar_mj)
                VALUES (@gridId, @cellLat, @cellLon, @Day, @TMean, @TMin, @Precip, @RootWetness, @SolarMj)
                ON CONFLICT (grid_id, day) DO UPDATE SET t_mean = EXCLUDED.t_mean, t_min = EXCLUDED.t_min, precip_mm = EXCLUDED.precip_mm,
                    root_wetness = EXCLUDED.root_wetness, solar_mj = EXCLUDED.solar_mj, fetched_at = now()
                """, days.Select(d => new { gridId, cellLat, cellLon, d.Day, d.TMean, d.TMin, d.Precip, d.RootWetness, d.SolarMj }));
        }
    }
}
