using System.Text.Json;

namespace Streaming;

public interface IGenreScheduleService
{
    Task<ScheduleResponse> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ScheduleSaveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetActiveGenresAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class GenreScheduleService(StreamingOptions options) : IGenreScheduleService
{
    public async Task<ScheduleResponse> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.SchedulePath))
            return new ScheduleResponse([]);

        await using var stream = File.OpenRead(options.SchedulePath);
        return await JsonSerializer.DeserializeAsync(stream, StreamingJsonContext.Default.ScheduleResponse, cancellationToken)
            ?? new ScheduleResponse([]);
    }

    public async Task SaveAsync(ScheduleSaveRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.SchedulePath) ?? ".");
        var response = new ScheduleResponse([.. request.Rules.Select(NormalizeRule)]);
        var tempPath = options.SchedulePath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, response, StreamingJsonContext.Default.ScheduleResponse, cancellationToken);

        File.Move(tempPath, options.SchedulePath, overwrite: true);
    }

    public async Task<IReadOnlySet<string>> GetActiveGenresAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadAsync(cancellationToken);
        HashSet<string> activeGenres = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in schedule.Rules)
        {
            if (!IsActive(rule, now))
                continue;

            foreach (var genre in rule.Genres.Select(g => g.Trim()).Where(g => g.Length > 0))
                activeGenres.Add(genre);
        }

        return activeGenres;
    }

    internal static bool IsActive(ScheduleRuleResponse rule, DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        if (TryParseDate(rule.Date, out var date) && date != DateOnly.FromDateTime(local))
            return false;

        if (!IsTimeActive(rule.StartTime, rule.EndTime, local.TimeOfDay))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.Date))
            return true;

        return IsRecurrenceActive(rule.RRule, local.DayOfWeek);
    }

    internal static bool TrackMatchesGenres(string? trackGenres, IReadOnlySet<string> allowedGenres) =>
        !string.IsNullOrWhiteSpace(trackGenres)
        && trackGenres.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(allowedGenres.Contains);

    private static ScheduleRuleResponse NormalizeRule(ScheduleRuleResponse rule)
    {
        var id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim();

        return rule with
        {
            Id = id,
            Name = rule.Name.Trim(),
            RRule = rule.RRule.Trim(),
            Date = string.IsNullOrWhiteSpace(rule.Date) ? null : rule.Date.Trim(),
            StartTime = string.IsNullOrWhiteSpace(rule.StartTime) ? null : rule.StartTime.Trim(),
            EndTime = string.IsNullOrWhiteSpace(rule.EndTime) ? null : rule.EndTime.Trim(),
            Genres = [.. rule.Genres
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    private static bool IsRecurrenceActive(string rrule, DayOfWeek dayOfWeek)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            return false;

        var parts = rrule.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => part[1], StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("FREQ", out var frequency))
            return false;

        if (!frequency.Equals("DAILY", StringComparison.OrdinalIgnoreCase)
            && !frequency.Equals("WEEKLY", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!parts.TryGetValue("BYDAY", out var byDay) || string.IsNullOrWhiteSpace(byDay))
            return true;

        var today = DayCode(dayOfWeek);
        return byDay.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => item.EndsWith(today, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTimeActive(string? startTime, string? endTime, TimeSpan time)
    {
        var minute = (time.Hours * 60) + time.Minutes;
        var start = TryParseMinute(startTime, out var parsedStart) ? parsedStart : 0;
        var end = TryParseMinute(endTime, out var parsedEnd) ? parsedEnd : 24 * 60;

        if (start == end)
            return true;

        return start < end
            ? minute >= start && minute < end
            : minute >= start || minute < end;
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var year)
            || !int.TryParse(parts[1], out var month)
            || !int.TryParse(parts[2], out var day))
            return false;

        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseMinute(string? value, out int minute)
    {
        minute = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minutes)
            || hour < 0
            || hour > 23
            || minutes < 0
            || minutes > 59)
            return false;

        minute = (hour * 60) + minutes;
        return true;
    }

    private static string DayCode(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU"
    };
}