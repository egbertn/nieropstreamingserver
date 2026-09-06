using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Streaming;

const string PdfContentType = "application/pdf";

var builder = WebApplication.CreateBuilder(args);

// Report readiness to systemd (Type=notify) and log to journald.
builder.Host.UseSystemd();

builder.Services.AddHttpClient<SonosService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IMusicReportService, MusicReportService>();
builder.Services.AddSingleton<IEmailReportService, EmailReportService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, StreamingJsonContext.Default));

// Sonos only plays plain HTTP; listen on every interface so the speaker can reach us.
builder.WebHost.ConfigureKestrel((c, options) =>
{
    var port = c.Configuration.GetValue("Streaming:Port", 8080);
    options.ListenAnyIP(port);
    options.Limits.MaxRequestBodySize = 1024L * 1024 * 300;
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
        var suffix = 2;
        while (File.Exists(destination))
        {
            destination = Path.Combine(musicRoot, $"{stem} ({suffix++}){extension}");
        }

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
    sb.Append("<p><a href=\"/playlist.m3u\">playlist.m3u</a> &middot; <a href=\"/api/tracks\">api/tracks</a> &middot; <a href=\"/report\">Rapport (PDF)</a></p><ul>");
    foreach (var track in EnumerateTracks())
    {
        var url = UrlFor(request, track);
        sb.Append("<li><a href=\"").Append(url).Append("\">")
          .Append(WebUtility.HtmlEncode(track)).Append("</a></li>");
    }
    sb.Append("</ul>");
    return Results.Content(sb.ToString(), "text/html", Encoding.UTF8);
});

app.MapGet("/report", (HttpRequest request, IMusicReportService reportService) =>
{
    if (request.Query.ContainsKey("download") || request.Query["format"] == "pdf" || request.Headers.Accept.ToString().Contains(PdfContentType))
    {
        var pdf = reportService.GeneratePdfReport();
        var download = request.Query.ContainsKey("download") && request.Query["download"] != "false";
        return Results.File(pdf, PdfContentType, fileDownloadName: download ? "muziek-rapport.pdf" : null);
    }

    var data = reportService.CollectReportData();
    var durationFormatted = data.TotalDuration.TotalHours >= 1
        ? $"{(int)data.TotalDuration.TotalHours}u {data.TotalDuration.Minutes:D2}m {data.TotalDuration.Seconds:D2}s"
        : $"{data.TotalDuration.Minutes:D2}m {data.TotalDuration.Seconds:D2}s";

    var sizeMb = data.TotalSizeBytes / (1024f * 1024f);
    var sizeFormatted = sizeMb >= 1000f ? $"{(sizeMb / 1024f):0.##} GB" : $"{sizeMb:0.#} MB";

    var sb = new StringBuilder();
    sb.Append($$"""
        <!doctype html>
        <html lang="nl">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Muziekcollectie Rapport</title>
        <style>
          :root { color-scheme: light dark; }
          body { font-family: system-ui, sans-serif; margin: 0; padding: 2rem; max-width: 52rem; }
          h1 { font-size: 1.5rem; margin-top: 0; color: #2d7d46; }
          .nav { margin-bottom: 1.5rem; font-size: 0.95rem; }
          .nav a { color: #2d7d46; text-decoration: none; }
          .nav a:hover { text-decoration: underline; }
          .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(130px, 1fr)); gap: 0.75rem; margin-bottom: 1.5rem; }
          .stat-card { border: 1px solid #8884; border-radius: 0.5rem; padding: 0.75rem 1rem; background: #8881; }
          .stat-card .label { font-size: 0.75rem; text-transform: uppercase; opacity: 0.7; font-weight: 600; }
          .stat-card .val { font-size: 1.25rem; font-weight: 700; margin-top: 0.25rem; }
          .actions { display: flex; flex-wrap: wrap; gap: 0.75rem; margin-bottom: 1.5rem; align-items: center; }
          .btn { display: inline-flex; align-items: center; justify-content: center; font-size: 1rem; padding: 0.6rem 1.2rem; border-radius: 0.5rem; border: 1px solid #8888; text-decoration: none; color: inherit; cursor: pointer; background: #8881; font-weight: 500; }
          .btn:hover { background: #8883; }
          .btn.primary { background: #2d7d46; color: #fff; border-color: #2d7d46; }
          .email-box { border: 1px solid #8884; border-radius: 0.5rem; padding: 1.25rem; background: #8881; margin-bottom: 1.5rem; }
          .email-box h2 { font-size: 1.1rem; margin-top: 0; margin-bottom: 0.75rem; }
          .email-row { display: flex; gap: 0.5rem; flex-wrap: wrap; }
          .email-row input[type=email] { flex: 1; min-width: 220px; font-size: 1rem; padding: 0.6rem 0.8rem; border-radius: 0.5rem; border: 1px solid #8888; background: inherit; color: inherit; }
          #emailStatus { margin-top: 0.75rem; font-size: 0.95rem; min-height: 1.4rem; font-weight: 500; }
          .preview-section { margin-top: 1.5rem; }
          .preview-section h2 { font-size: 1.1rem; margin-bottom: 0.75rem; }
          iframe { width: 100%; height: 600px; border: 1px solid #8884; border-radius: 0.5rem; }
        </style>
        </head>
        <body>
        <div class="nav">
          <a href="/">&larr; Terug naar Muziekbediening</a> &middot; <a href="/tracks">Lijstweergave</a>
        </div>
        <h1>Muziekcollectie Rapport</h1>

        <div class="stats-grid">
          <div class="stat-card"><div class="label">Nummers</div><div class="val">{{data.Tracks.Count}}</div></div>
          <div class="stat-card"><div class="label">Speelduur</div><div class="val">{{durationFormatted}}</div></div>
          <div class="stat-card"><div class="label">Grootte</div><div class="val">{{sizeFormatted}}</div></div>
          <div class="stat-card"><div class="label">Vrij / CC</div><div class="val" style="color: #2d7d46;">{{data.FreeCount}}</div></div>
          <div class="stat-card"><div class="label">Beschermd</div><div class="val" style="color: #dc2626;">{{data.ProtectedCount}}</div></div>
          <div class="stat-card"><div class="label">Geen tags</div><div class="val">{{data.UnspecifiedCount}}</div></div>
        </div>

        <div class="actions">
          <a href="/report/pdf?download=true" class="btn primary">📥 Download PDF</a>
          <a href="/report/pdf" target="_blank" class="btn">📄 Open PDF in nieuw tabblad</a>
        </div>

        <div class="email-box">
          <h2>✉️ Rapport per E-mail versturen</h2>
          <form id="emailForm" onsubmit="event.preventDefault(); sendEmail();">
            <div class="email-row">
              <input type="email" id="emailAddress" placeholder="naam@voorbeeld.nl" required>
              <button type="submit" id="btnSendEmail" class="btn primary">Verstuur PDF</button>
            </div>
            <div id="emailStatus"></div>
          </form>
        </div>

        <div class="preview-section">
          <h2>Voorvertoning van PDF</h2>
          <iframe src="/report/pdf" title="PDF Rapport Voorvertoning"></iframe>
        </div>

        <script>
        async function sendEmail() {
          const emailInput = document.getElementById('emailAddress');
          const statusEl = document.getElementById('emailStatus');
          const btn = document.getElementById('btnSendEmail');
          const email = emailInput.value.trim();

          if (!email) {
            statusEl.textContent = 'Vul een geldig e-mailadres in.';
            statusEl.style.color = '#ef4444';
            return;
          }

          btn.disabled = true;
          statusEl.textContent = 'Bezig met versturen...';
          statusEl.style.color = 'inherit';

          try {
            const token = localStorage.getItem('apiToken');
            const res = await fetch('/report/email', {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                ...(token ? { 'X-Api-Token': token } : {})
              },
              body: JSON.stringify({ email: email })
            });

            const data = await res.json();
            if (res.ok && data.success) {
              statusEl.textContent = '✓ ' + data.message;
              statusEl.style.color = '#2d7d46';
            } else {
              statusEl.textContent = '✗ ' + (data.error || data.message || 'Verzenden mislukt');
              statusEl.style.color = '#ef4444';
            }
          } catch (err) {
            statusEl.textContent = '✗ Fout: ' + err.message;
            statusEl.style.color = '#ef4444';
          } finally {
            btn.disabled = false;
          }
        }
        </script>
        </body>
        </html>
        """);

    return Results.Content(sb.ToString(), "text/html", Encoding.UTF8);
});

app.MapGet("/report/pdf", (IMusicReportService reportService, bool download = false) =>
{
    var pdf = reportService.GeneratePdfReport();
    return Results.File(pdf, PdfContentType, fileDownloadName: download ? "muziek-rapport.pdf" : null);
});

app.MapGet("/report/download", (IMusicReportService reportService) =>
{
    var pdf = reportService.GeneratePdfReport();
    return Results.File(pdf, PdfContentType, fileDownloadName: "muziek-rapport.pdf");
});

app.MapPost("/report/email", async (HttpRequest request, IEmailReportService emailService, CancellationToken ct) =>
{
    string? email = null;
    if (request.HasJsonContentType())
    {
        var body = await request.ReadFromJsonAsync(StreamingJsonContext.Default.EmailReportRequest, ct);
        email = body?.Email;
    }
    else if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        email = form["email"].ToString();
    }
    else
    {
        email = request.Query["email"].ToString();
    }

    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new EmailReportResponse(false, "E-mailadres ontbreekt"));

    try
    {
        await emailService.SendReportAsync(email, ct);
        return Results.Ok(new EmailReportResponse(true, $"Het rapport is succesvol gemaild naar {email}"));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new EmailReportResponse(false, ex.Message));
    }
});

app.MapPost("/api/report/email", async (EmailReportRequest request, IEmailReportService emailService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.BadRequest(new EmailReportResponse(false, "E-mailadres ontbreekt"));

    try
    {
        await emailService.SendReportAsync(request.Email, ct);
        return Results.Ok(new EmailReportResponse(true, $"Het rapport is succesvol gemaild naar {request.Email}"));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new EmailReportResponse(false, ex.Message));
    }
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

await app.RunAsync();
