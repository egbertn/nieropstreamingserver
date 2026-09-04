using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Streaming;

var builder = WebApplication.CreateBuilder(args);

// Report readiness to systemd (Type=notify) and log to journald.
builder.Host.UseSystemd();

builder.Services.AddHttpClient<SonosService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, StreamingJsonContext.Default));

// Sonos only plays plain HTTP; listen on every interface so the speaker can reach us.
builder.WebHost.ConfigureKestrel((c, options) =>
{
    var port = c.Configuration.GetValue("Streaming:Port", 8080);
    options.ListenAnyIP(port);
    options.Limits.MaxRequestBodySize = 1024L * 1024 * 1024;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1024L * 1024 * 300;
});

var app = builder.Build();

var musicRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "music");
Directory.CreateDirectory(musicRoot);

var contentTypes = new FileExtensionContentTypeProvider(new Dictionary<string, string>
{
    [".mp3"] = "audio/mpeg",
    [".m4a"] = "audio/mp4",
    [".flac"] = "audio/flac",
    [".wav"] = "audio/wav",
    [".ogg"] = "audio/ogg"
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(musicRoot),
    RequestPath = "/music",
    ContentTypeProvider = contentTypes,
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "public, max-age=3600";
    }
});

// The speaker cannot send headers, so only /api is guarded; /music stays open for playback.
// The custom header also forces a CORS preflight, which blocks requests from other sites.
var apiToken = app.Configuration["Streaming:ApiToken"];
if (string.IsNullOrWhiteSpace(apiToken))
    app.Logger.LogWarning("Streaming:ApiToken is not set; anyone on the network can upload and delete files.");

app.Use(async (context, next) =>
{
    if (!string.IsNullOrWhiteSpace(apiToken) && context.Request.Path.StartsWithSegments("/api"))
    {
        var provided = context.Request.Headers["X-Api-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(apiToken)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("Invalid or missing token"),
                StreamingJsonContext.Default.ErrorResponse);
            return;
        }
    }

    await next();
});

string[] audioExtensions = [".mp3", ".m4a", ".flac", ".wav", ".ogg"];

IEnumerable<string> EnumerateTracks() =>
    Directory.Exists(musicRoot)
        ? Directory.EnumerateFiles(musicRoot, "*", SearchOption.AllDirectories)
            .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(musicRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
        : [];

string UrlFor(HttpRequest request, string relativePath)
{
    var encoded = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
    return $"{request.Scheme}://{request.Host}/music/{encoded}";
}

app.MapGet("/api/tracks", (HttpRequest request) =>
    EnumerateTracks().Select(t => new TrackResponse(
        t,
        Path.GetFileNameWithoutExtension(t),
        UrlFor(request, t))).ToArray());

app.MapPost("/api/tracks", async (HttpRequest request, bool force = false) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new ErrorResponse("Geen upload gevonden"));

    var form = await request.ReadFormAsync();
    List<string> saved = [];
    List<string> skipped = [];
    List<string> flagged = [];

    foreach (var file in form.Files)
    {
        var name = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(name);
        var isAudio = file.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

        if (string.IsNullOrWhiteSpace(name) || !isAudio || !audioExtensions.Contains(extension, StringComparer.InvariantCultureIgnoreCase))
        {
            skipped.Add(file.FileName);
            continue;
        }

        var destination = Path.Combine(musicRoot, name);
        // Only write straight into the music folder; paths from the client are untrusted.
        if (Path.GetDirectoryName(Path.GetFullPath(destination)).CompareTo(Path.GetFullPath(musicRoot), StringComparison.InvariantCultureIgnoreCase) != 0)
        {
            skipped.Add(file.FileName);
            continue;
        }

        // Never silently truncate a track that is already there.
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; File.Exists(destination); i++)
            destination = Path.Combine(musicRoot, $"{stem} ({i}){extension}");

        await using (var stream = File.Create(destination))
            await file.CopyToAsync(stream);

        // Heuristic only: absence of these markers is not proof a track is license-free.
        var reason = CopyrightCheck.Inspect(destination);
        if (reason is not null && !force)
        {
            File.Delete(destination);
            flagged.Add($"{name}: {reason}");
            continue;
        }

        saved.Add(Path.GetFileName(destination));
    }

    return Results.Ok(new UploadResponse(saved, skipped, flagged));
}).DisableAntiforgery();

app.MapDelete("/api/tracks/{*path}", (string path) =>
{
    var full = Path.GetFullPath(Path.Combine(musicRoot, path));
    if (!full.StartsWith(Path.GetFullPath(musicRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return Results.BadRequest(new ErrorResponse("Ongeldig pad"));

    if (!audioExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
        return Results.BadRequest(new ErrorResponse("Geen muziekbestand"));

    if (!File.Exists(full))
        return Results.NotFound(new ErrorResponse("Bestand bestaat niet"));

    File.Delete(full);
    return Results.Ok(new DeletedResponse(path));
});

app.MapGet("/playlist.m3u", (HttpRequest request) =>
{
    var sb = new StringBuilder("#EXTM3U\n");
    foreach (var track in EnumerateTracks())
    {
        sb.Append("#EXTINF:-1,").Append(Path.GetFileNameWithoutExtension(track)).Append('\n');
        sb.Append(UrlFor(request, track)).Append('\n');
    }
    return Results.Text(sb.ToString(), "audio/x-mpegurl", Encoding.UTF8);
});

app.MapGet("/tracks", (HttpRequest request) =>
{
    var sb = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>Muziek</title><h1>Muziek</h1>");
    sb.Append("<p><a href=\"/playlist.m3u\">playlist.m3u</a> &middot; <a href=\"/api/tracks\">api/tracks</a></p><ul>");
    foreach (var track in EnumerateTracks())
    {
        var url = UrlFor(request, track);
        sb.Append("<li><a href=\"").Append(url).Append("\">")
          .Append(WebUtility.HtmlEncode(track)).Append("</a></li>");
    }
    sb.Append("</ul>");
    return Results.Content(sb.ToString(), "text/html", Encoding.UTF8);
});

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", EnumerateTracks().Count())));

app.MapGet("/api/speakers", (SonosService sonos, bool refresh = false) => sonos.DiscoverAsync(refresh));

app.MapGet("/api/state", async (SonosService sonos, string? speaker) =>
{
    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse("Geen speaker gevonden"));

    var state = await sonos.GetTransportStateAsync(target);
    var position = await sonos.GetPositionInfoAsync(target);
    return Results.Ok(new StateResponse(
        target.Name,
        state,
        position?.Title,
        position?.Elapsed.TotalSeconds,
        position?.Duration.TotalSeconds));
});

app.MapPost("/api/play", async (SonosService sonos, HttpRequest request, string? speaker, bool shuffle = false) =>
{
    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse("Geen speaker gevonden"));

    var tracks = EnumerateTracks().ToArray();
    if (tracks.Length == 0)
        return Results.BadRequest(new ErrorResponse("Geen muziek gevonden"));

    try
    {
        // The files on the server are the playlist; the queue is rebuilt from them every time.
        await sonos.ClearQueueAsync(target);
        foreach (var t in tracks)
            await sonos.QueueUriAsync(target, UrlFor(request, t), Path.GetFileNameWithoutExtension(t));

        await sonos.SelectQueueAsync(target);
        await sonos.SetPlayModeAsync(target, shuffle ? "SHUFFLE" : "REPEAT_ALL");
        await sonos.PlayAsync(target);
        return Results.Ok(new PlayResponse(target.Name, tracks.Length));
    }
    catch (SonosException ex)
    {
        return Results.Json(
            new SonosErrorResponse(ex.Message, ex.ErrorCode),
            StreamingJsonContext.Default.SonosErrorResponse,
            statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapPost("/api/pause", (SonosService sonos, string? speaker) => Command(sonos, speaker, s => sonos.PauseAsync(s)));
app.MapPost("/api/stop", (SonosService sonos, string? speaker) => Command(sonos, speaker, s => sonos.StopAsync(s)));
app.MapPost("/api/next", (SonosService sonos, string? speaker) => Command(sonos, speaker, s => sonos.NextAsync(s)));
app.MapPost("/api/previous", (SonosService sonos, string? speaker) => Command(sonos, speaker, s => sonos.PreviousAsync(s)));
app.MapPost("/api/volume", (SonosService sonos, int level, string? speaker) => Command(sonos, speaker, s => sonos.SetVolumeAsync(s, level)));

async Task<IResult> Command(SonosService sonos, string? speaker, Func<SonosSpeaker, Task> action)
{
    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse("Geen speaker gevonden"));

    try
    {
        await action(target);
    }
    catch (SonosException ex)
    {
        return Results.Json(
            new SonosErrorResponse(ex.Message, ex.ErrorCode),
            StreamingJsonContext.Default.SonosErrorResponse,
            statusCode: StatusCodes.Status409Conflict);
    }

    return Results.Ok(new SpeakerResponse(target.Name));
}

app.Run();
