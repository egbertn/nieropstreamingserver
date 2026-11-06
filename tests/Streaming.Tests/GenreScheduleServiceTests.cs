namespace Streaming.Tests;

public sealed class GenreScheduleServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetActiveGenres_NoRules_ReturnsNullSoAllTracksCanPlay()
    {
        var service = CreateService();

        var genres = await service.GetActiveGenresAsync(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

        Assert.Null(genres);
    }

    [Fact]
    public async Task GetActiveGenres_WeeklyRuleInsideWindow_ReturnsGenres()
    {
        var service = CreateService();
        await service.SaveAsync(new ScheduleSaveRequest([
            new ScheduleRuleResponse("", "Werkweek", "FREQ=WEEKLY;BYDAY=MO,TU,TH,FR", null, "10:00", "17:00", ["Jazz", "Soul"])
        ]));

        var genres = await service.GetActiveGenresAsync(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

        Assert.NotNull(genres);
        Assert.Contains("Jazz", genres);
        Assert.Contains("Soul", genres);
    }

    [Fact]
    public async Task GetActiveGenres_OutsideMatchingRule_ReturnsNullSoAllTracksCanPlay()
    {
        var service = CreateService();
        await service.SaveAsync(new ScheduleSaveRequest([
            new ScheduleRuleResponse("", "Ochtend", "FREQ=DAILY", null, "10:00", "17:00", ["Jazz"])
        ]));

        var genres = await service.GetActiveGenresAsync(new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero));

        Assert.Null(genres);
    }

    [Fact]
    public async Task GetActiveGenres_DateRuleOverridesRecurrenceDate()
    {
        var service = CreateService();
        await service.SaveAsync(new ScheduleSaveRequest([
            new ScheduleRuleResponse("", "Kerst", "", "2026-12-25", null, null, ["Klassiek"])
        ]));

        var christmas = await service.GetActiveGenresAsync(new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero));
        var otherDay = await service.GetActiveGenresAsync(new DateTimeOffset(2026, 12, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.NotNull(christmas);
        Assert.Contains("Klassiek", christmas);
        Assert.Null(otherDay);
    }

    [Fact]
    public void TrackMatchesGenres_UsesCommaSeparatedEffectiveGenreList()
    {
        Assert.True(GenreScheduleService.TrackMatchesGenres("Pop, Jazz", new HashSet<string>(["jazz"], StringComparer.OrdinalIgnoreCase)));
        Assert.False(GenreScheduleService.TrackMatchesGenres("Rock", new HashSet<string>(["Jazz"], StringComparer.OrdinalIgnoreCase)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private GenreScheduleService CreateService() => new(new StreamingOptions
    {
        MusicRoot = Path.Combine(_directory, "music"),
        DatabasePath = Path.Combine(_directory, "streaming.db"),
        SchedulePath = Path.Combine(_directory, "schedule.json"),
        SmtpPassword = "smtppass",
        SmtpUsername = "smtpuser"
    });
}