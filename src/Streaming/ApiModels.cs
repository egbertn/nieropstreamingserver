using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streaming;

public sealed record ErrorResponse(string Error);

public sealed record SonosErrorResponse(string Error, int Code);

public sealed record TrackMetadata(
    string Performers,
    string Album,
    uint Year,
    string Genres,
    string Copyright,
    string Publisher,
    string Isrc,
    string? Mood,
    int? Energy,
    int? TempoBpm,
    bool? Instrumental);

public sealed record TrackResponse(string Path, string Title, string Url, string Genre, TrackMetadata Metadata);

public sealed record TracksPageResponse(IReadOnlyList<TrackResponse> Items, int Total);

public sealed record GenreUpdateRequest(string Path, string? Genre);

public sealed record UploadResponse(IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Flagged, IReadOnlyList<string> Failed);

public sealed record DeletedResponse(string Deleted);

public sealed record HealthResponse(string Status, int Tracks);

public sealed record StateResponse(
    string Speaker,
    string State,
    string? Title,
    double? Elapsed,
    double? Duration,
    bool WaitingForSchedule = false,
    bool SchedulePaused = false);

public sealed record PlayResponse(string Speaker, int Tracks);

/// <summary>The track filter to play: a name fragment and/or genres (a track matches any of them).</summary>
public sealed record PlaySelectionRequest(string? Name, IReadOnlyList<string>? Genres);

public sealed record SpeakerResponse(string Speaker);

public sealed record EmailReportRequest(string Email);

public sealed record EmailReportResponse(bool Success, string Message);

public sealed record ScheduleRuleResponse(
    string Id,
    string Name,
    string RRule,
    string? Date,
    string? StartTime,
    string? EndTime,
    IReadOnlyList<string> Genres);

public sealed record ScheduleResponse(IReadOnlyList<ScheduleRuleResponse> Rules);

public sealed record ScheduleSaveRequest(IReadOnlyList<ScheduleRuleResponse> Rules);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SonosErrorResponse))]
[JsonSerializable(typeof(TrackResponse[]))]
[JsonSerializable(typeof(TracksPageResponse))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(DeletedResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StateResponse))]
[JsonSerializable(typeof(PlayResponse))]
[JsonSerializable(typeof(PlaySelectionRequest))]
[JsonSerializable(typeof(SpeakerResponse))]
[JsonSerializable(typeof(EmailReportRequest))]
[JsonSerializable(typeof(EmailReportResponse))]
[JsonSerializable(typeof(GenreUpdateRequest))]
[JsonSerializable(typeof(ScheduleResponse))]
[JsonSerializable(typeof(ScheduleSaveRequest))]
[JsonSerializable(typeof(ScheduleRuleResponse))]
[JsonSerializable(typeof(ScheduleRuleResponse[]))]
[JsonSerializable(typeof(SonosSpeaker[]))]
[JsonSerializable(typeof(IReadOnlyList<SonosSpeaker>))]
[JsonSerializable(typeof(TrackMetadata))]
public partial class StreamingJsonContext : JsonSerializerContext;