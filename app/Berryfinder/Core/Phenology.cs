// Model B, version 1: a heuristic seasonal window for berry development.
//
// Without verified flowering and ripeness observations there is nothing to train on, so this layer
// places the season by growing degree days (base +5 °C, accumulated from 1 April) at the nearest
// FMI weather station. The thresholds are rough values for the Oulu region and are the first thing
// the pilot's field observations will calibrate. The output is a stage and an availability window,
// never a yield or an amount.
public static class Phenology
{
    public const string Version = "season-heuristic-1.0";

    public sealed record DailyWeather(DateOnly Day, double? TMean, double? TMin, double? Precip, double? SnowCm, double? RootWetness = null);

    public sealed record Thresholds(double FlowerStart, double FlowerEnd, double RipeningStart, double RipeStart, double LateStart, int LastMonth, int LastDay);

    // GDD5 thresholds per species, and the calendar date after which the season is treated as over
    public static readonly IReadOnlyDictionary<string, Thresholds> Windows = new Dictionary<string, Thresholds>
    {
        [Species.Bilberry] = new(50, 200, 550, 650, 1000, 9, 25),
        [Species.Lingonberry] = new(200, 380, 850, 950, 1150, 10, 31),
    };

    public sealed record Note(string Key, string En, string Fi);

    public sealed record Result(string Stage, string Availability, double? Gdd, DateOnly? AsOf, IReadOnlyList<Note> Notes, string Basis, string Source = "none", double? RootWetness = null);

    public static double GrowingDegreeDays(IEnumerable<DailyWeather> days, int year, DateOnly asOf)
    {
        var start = new DateOnly(year, 4, 1);
        return days.Where(d => d.Day >= start && d.Day <= asOf && d.TMean is not null).Sum(d => Math.Max(0, d.TMean!.Value - 5));
    }

    // Station observations (FMI) are preferred. Where the station has gaps, the NASA POWER model grid fills in and the
    // result says so, because a grid cell is a model of the area, not a reading from a weather station.
    public static Result Estimate(string species, DateOnly date, IReadOnlyList<DailyWeather> weather, double? normalJunJulPrecip, IReadOnlyList<DailyWeather>? grid = null)
    {
        var station = EstimateFrom(species, date, weather, normalJunJulPrecip);
        var wetness = RecentWetness(grid, date);

        if (station.Stage != "unknown")
        {
            return station with { Source = station.Gdd is null ? "none" : "station", RootWetness = wetness };
        }

        if (grid is null || grid.Count == 0)
        {
            return station;
        }

        var modelled = EstimateFrom(species, date, grid, normalJunJulPrecip: null);

        if (modelled.Stage == "unknown")
        {
            return station with { RootWetness = wetness };
        }

        var note = new Note("grid", "Weather station data was incomplete, so this uses NASA POWER's modelled grid (about 55 km cells); expect a larger error",
            "Sääaseman tiedoissa oli aukkoja, joten käytetään NASA POWERin mallinnettua ruudukkoa (noin 55 km ruudut); virhe voi olla suurempi");
        return modelled with
        {
            Notes = [note, .. modelled.Notes],
            Source = modelled.Gdd is null ? "none" : "nasa_power",
            RootWetness = wetness,
            Basis = "Growing degree days (base +5 °C) from the NASA POWER MERRA-2 model grid; heuristic thresholds, not yet calibrated with field data",
        };
    }

    private static double? RecentWetness(IReadOnlyList<DailyWeather>? grid, DateOnly date)
    {
        var recent = grid?.Where(d => d.Day <= date && d.Day > date.AddDays(-14) && d.RootWetness is not null).Select(d => d.RootWetness!.Value).ToList();
        return recent is { Count: >= 7 } ? Math.Round(recent.Average(), 2) : null;
    }

    private static Result EstimateFrom(string species, DateOnly date, IReadOnlyList<DailyWeather> weather, double? normalJunJulPrecip)
    {
        var w = Windows[species];
        var seasonYearDays = weather.Where(d => d.Day.Year == date.Year && d.Day <= date).ToList();
        var notes = new List<Note>();

        // Winter and early spring need no weather: the plants are dormant
        if (date.Month < 5 || (date.Month == w.LastMonth && date.Day > w.LastDay) || date.Month > w.LastMonth)
        {
            return new Result("dormant", "over", null, null, notes,
                "Outside the berry season by calendar date");
        }

        var lastObserved = seasonYearDays.Count == 0 ? (DateOnly?)null : seasonYearDays.Max(d => d.Day);
        var coverage = seasonYearDays.Count(d => d.Day >= new DateOnly(date.Year, 4, 1) && d.TMean is not null);
        var expected = Math.Max(1, date.DayNumber - new DateOnly(date.Year, 4, 1).DayNumber + 1);

        if (lastObserved is null || coverage < expected * 0.8 || date.DayNumber - lastObserved.Value.DayNumber > 5)
        {
            return new Result("unknown", "unknown", null, lastObserved, notes,
                "Not enough recent FMI station data to place the season");
        }

        var gdd = GrowingDegreeDays(seasonYearDays, date.Year, date);

        var stage = gdd < w.FlowerStart ? "before_flowering"
            : gdd < w.FlowerEnd ? "flowering"
            : gdd < w.RipeningStart ? "unripe"
            : gdd < w.RipeStart ? "ripening"
            : gdd < w.LateStart ? "ripe"
            : "late";

        var availability = stage switch
        {
            "before_flowering" or "flowering" or "unripe" => "not_yet",
            "ripening" => "starting",
            "ripe" => "peak",
            _ => "late",
        };

        // A hard frost during flowering damages flowers and is a known cause of poor years
        var flowering = seasonYearDays.Where(d => d.TMin is not null).ToList();
        var flowerStartDay = DayReaching(seasonYearDays, date.Year, w.FlowerStart);
        var flowerEndDay = DayReaching(seasonYearDays, date.Year, w.FlowerEnd) ?? date;

        if (flowerStartDay is { } from)
        {
            var frostDays = flowering.Count(d => d.Day >= from && d.Day <= flowerEndDay && d.TMin <= -2.0);
            if (frostDays > 0)
            {
                notes.Add(new Note("frost", $"{frostDays} night(s) of frost (≤ −2 °C) at the station during flowering: yields may be reduced",
                    $"Asemalla {frostDays} pakkasyö(tä) (≤ −2 °C) kukinnan aikana: sato voi jäädä heikommaksi"));
            }
        }

        if (normalJunJulPrecip is { } normal && date >= new DateOnly(date.Year, 8, 1))
        {
            var junJul = seasonYearDays.Where(d => d.Day.Month is 6 or 7 && d.Precip is not null).Sum(d => d.Precip!.Value);
            if (junJul < 0.6 * normal)
            {
                notes.Add(new Note("dry", $"June–July rainfall was {junJul:0} mm, well below the station's recent normal ({normal:0} mm)",
                    $"Kesä–heinäkuun sademäärä oli {junJul:0} mm, selvästi alle aseman tavanomaisen ({normal:0} mm)"));
            }
        }

        if (seasonYearDays.Where(d => d.Day > date.AddDays(-7)).Any(d => d.SnowCm >= 5))
        {
            notes.Add(new Note("snow", "Snow cover reported at the station this week", "Asemalla raportoitu lumipeite tällä viikolla"));
            availability = "over";
        }

        return new Result(stage, availability, Math.Round(gdd), lastObserved, notes,
            "Growing degree days (base +5 °C) at the nearest FMI weather station; heuristic thresholds, not yet calibrated with field data");
    }

    private static DateOnly? DayReaching(IReadOnlyList<DailyWeather> days, int year, double threshold)
    {
        var total = 0.0;
        foreach (var d in days.Where(d => d.Day >= new DateOnly(year, 4, 1) && d.TMean is not null).OrderBy(d => d.Day))
        {
            total += Math.Max(0, d.TMean!.Value - 5);
            if (total >= threshold)
            {
                return d.Day;
            }
        }

        return null;
    }
}
