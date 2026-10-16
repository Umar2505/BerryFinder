using Xunit;

// These fixtures are real responses recorded from the live services (NASA POWER and FMI), unedited,
// so the tests check the parsers against what the providers actually send.
public class ParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded", name));

    [Fact]
    public void NasaPower_parses_daily_values_and_dates()
    {
        var days = NasaPowerParser.Parse(Fixture("nasa-power-oulu-2025-06.json"));

        Assert.Equal(7, days.Count);
        Assert.Equal(new DateOnly(2025, 6, 1), days[0].Day);
        Assert.Equal(new DateOnly(2025, 6, 7), days[^1].Day);
        Assert.All(days, d =>
        {
            Assert.NotNull(d.TMean);
            Assert.InRange(d.TMean!.Value, -5, 30);
            Assert.NotNull(d.RootWetness);
            Assert.InRange(d.RootWetness!.Value, 0, 1);
            Assert.True(d.Precip >= 0);
        });
    }

    [Fact]
    public void NasaPower_treats_the_fill_value_as_missing_not_as_minus_999_degrees()
    {
        const string json = """
            {"header":{"fill_value":-999.0},"properties":{"parameter":{
              "T2M":{"20250601":-999.0,"20250602":10.5},"PRECTOTCORR":{"20250601":-999.0,"20250602":0.0},
              "T2M_MIN":{"20250601":-999.0,"20250602":4.0},"GWETROOT":{"20250601":-999.0,"20250602":0.6},"ALLSKY_SFC_SW_DWN":{"20250601":-999.0,"20250602":15.0}}}}
            """;
        var days = NasaPowerParser.Parse(json);

        Assert.Single(days);
        Assert.Equal(new DateOnly(2025, 6, 2), days[0].Day);
        Assert.Equal(10.5, days[0].TMean);
    }

    [Fact]
    public void NasaPower_returns_nothing_for_an_error_payload()
    {
        Assert.Empty(NasaPowerParser.Parse("""{"message":"Invalid request"}"""));
    }

    [Fact]
    public void NasaPower_grid_id_is_stable_for_every_point_in_one_cell()
    {
        Assert.Equal(NasaPowerParser.GridId(64.99, 25.62), NasaPowerParser.GridId(65.10, 25.40));
        Assert.NotEqual(NasaPowerParser.GridId(64.99, 25.62), NasaPowerParser.GridId(62.0, 25.62));
    }

    [Fact]
    public void Fmi_parses_station_position_and_daily_values()
    {
        var (station, days) = FmiParser.Parse(Fixture("fmi-daily-vihreasaari-2025-06.xml"));

        Assert.NotNull(station);
        Assert.InRange(station!.Value.Lat, 64.9, 65.1);
        Assert.InRange(station.Value.Lon, 25.3, 25.6);
        Assert.Equal(4, days.Count);
        Assert.Equal(new DateOnly(2025, 6, 1), days[0].Day);
        Assert.All(days, d => Assert.NotNull(d.TMean));
    }

    [Fact]
    public void Fmi_treats_minus_one_precipitation_as_zero_and_NaN_as_missing()
    {
        var xml = """
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:gml="http://www.opengis.net/gml/3.2" xmlns:BsWfs="http://xml.fmi.fi/schema/wfs/2.0">
              <wfs:member><BsWfs:BsWfsElement><BsWfs:Location><gml:Point><gml:pos>65.0 25.4</gml:pos></gml:Point></BsWfs:Location>
                <BsWfs:Time>2025-06-01T00:00:00Z</BsWfs:Time><BsWfs:ParameterName>rrday</BsWfs:ParameterName><BsWfs:ParameterValue>-1.0</BsWfs:ParameterValue></BsWfs:BsWfsElement></wfs:member>
              <wfs:member><BsWfs:BsWfsElement><BsWfs:Location><gml:Point><gml:pos>65.0 25.4</gml:pos></gml:Point></BsWfs:Location>
                <BsWfs:Time>2025-06-01T00:00:00Z</BsWfs:Time><BsWfs:ParameterName>tday</BsWfs:ParameterName><BsWfs:ParameterValue>NaN</BsWfs:ParameterValue></BsWfs:BsWfsElement></wfs:member>
            </wfs:FeatureCollection>
            """;
        var (_, days) = FmiParser.Parse(xml);

        Assert.Single(days);
        Assert.Equal(0, days[0].Precip);
        Assert.Null(days[0].TMean);
    }
}
