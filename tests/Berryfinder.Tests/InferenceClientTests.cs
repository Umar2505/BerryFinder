using Xunit;

// The app's reading of inference-service responses. The fixtures are real responses recorded from the service (a 1 km analysis
// near Sanginsuu, Oulu), with the cell polygons left out as ?include=summary does.
public class InferenceClientTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inference", name));

    [Fact]
    public void A_finished_analysis_reads_its_berries_and_pins()
    {
        var job = InferenceClient.ParseJob(Fixture("succeeded.json"));

        Assert.Equal("succeeded", job.Status);
        Assert.Null(job.Error);
        var summary = Assert.IsType<AnalysisSummary>(job.Summary);
        Assert.Equal("nordic-gbif-1.0", summary.ModelVersion);
        Assert.Equal("gbif", summary.LabelMode);
        Assert.Equal(100, summary.StepM);
        Assert.Equal(10, summary.Species.Count);
        Assert.All(summary.Species, s => Assert.Contains(s.LocalDifferences, new[] { "clear", "weak", "minimal" }));
        Assert.NotEmpty(summary.Pins);
        Assert.All(summary.Pins, p =>
        {
            Assert.InRange(p.LocalRank, 80, 100);                 // pins come from the area's best fifth
            Assert.InRange(p.DistanceM, 0, 1000);                 // inside the 1 km circle
            Assert.InRange(p.Lat, 64.9, 65.1);
            Assert.False(string.IsNullOrEmpty(p.HabitatEn));
            Assert.False(string.IsNullOrEmpty(p.HabitatFi));
        });
        Assert.Equal(summary.Pins.Count, summary.Pins.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void A_running_analysis_reports_the_real_stage()
    {
        var job = InferenceClient.ParseJob(Fixture("running.json"));
        Assert.Equal("running", job.Status);
        Assert.Equal("soil", job.Progress.Stage);
        Assert.Equal(2, job.Progress.CompletedStages);
        Assert.Null(job.Summary);
    }

    [Fact]
    public void A_failed_analysis_carries_a_message_for_people()
    {
        var job = InferenceClient.ParseJob(Fixture("failed.json"));
        Assert.Equal("failed", job.Status);
        Assert.Equal("data_unavailable:climate", job.Error!.Code);
        Assert.Contains("Try again", job.Error.Message);
        Assert.Null(job.Summary);
    }

    [Fact]
    public async Task An_unreachable_service_is_reported_as_offline()
    {
        var client = new InferenceClient("http://127.0.0.1:9");     // nothing listens there
        Assert.False(await client.HealthyAsync());
        await Assert.ThrowsAsync<InferenceUnavailableException>(() => client.StartAsync(65.0, 25.5, 3000, null));
    }
}
