using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streaming;

public sealed record ErrorResponse(string Error);

public sealed record SonosErrorResponse(string Error, int Code);

public sealed record TrackResponse(string Path, string Title, string Url);

public sealed record UploadResponse(IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Flagged);

public sealed record DeletedResponse(string Deleted);

public sealed record HealthResponse(string Status, int Tracks);

public sealed record StateResponse(
    string Speaker,
    string State,
    string? Title,
    double? Elapsed,
    double? Duration);

public sealed record PlayResponse(string Speaker, int Tracks);

public sealed record SpeakerResponse(string Speaker);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SonosErrorResponse))]
[JsonSerializable(typeof(TrackResponse[]))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(DeletedResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StateResponse))]
[JsonSerializable(typeof(PlayResponse))]
[JsonSerializable(typeof(SpeakerResponse))]
[JsonSerializable(typeof(SonosSpeaker[]))]
[JsonSerializable(typeof(IReadOnlyList<SonosSpeaker>))]
public partial class StreamingJsonContext : JsonSerializerContext;