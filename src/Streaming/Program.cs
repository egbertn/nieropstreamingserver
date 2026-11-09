using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Streaming;

var builder = WebApplication.CreateBuilder(args);
LoggingDefaults.Apply(builder.Configuration);

// Computed once at startup so services don't each recompute paths from IConfiguration/IWebHostEnvironment.
var streamingOptions = StreamingOptions.FromConfiguration(builder.Configuration, builder.Environment);

// Report readiness to systemd (Type=notify) and log to journald.
builder.Host.UseSystemd();
var services = builder.Services;
services.AddSingleton(streamingOptions);
services.AddSingleton<AudioConverter>();
// Singleton (i.p.v. transient typed client) zodat de speakercache van 5 minuten gedeeld wordt
// tussen requests en de PlaybackAuditWorker; anders draait elke poll een volledige discovery.
services.AddHttpClient(nameof(SonosService), c => c.Timeout = TimeSpan.FromSeconds(10));
services.AddSingleton(sp => new SonosService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SonosService)),
    sp.GetRequiredService<StreamingOptions>(),
    sp.GetRequiredService<ILogger<SonosService>>()));
services.AddSingleton<IMusicReportService, MusicReportService>();
services.AddSingleton<IAuditStorage, AuditStorage>();
services.AddSingleton<IPlayAuditReportService, PlayAuditReportService>();
services.AddSingleton<IEmailReportService, EmailReportService>();
services.AddSingleton<IRandomSource, SharedRandomSource>();
services.AddSingleton<IBroadcastPlaybackService, BroadcastPlaybackService>();
services.AddSingleton<IGenreScheduleService, GenreScheduleService>();
services.AddHostedService<PlaybackAuditWorker>();
services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, StreamingJsonContext.Default));

// Sonos only plays plain HTTP; listen on every interface so the speaker can reach us.
builder.WebHost.ConfigureKestrel((c, options) =>
{
    options.ListenAnyIP(streamingOptions.Port);
    options.Limits.MaxRequestBodySize = 1024L * 1024 * 300;
});

services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1024L * 1024 * 300;
});

var app = builder.Build();

var musicRoot = streamingOptions.MusicRoot;
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
var apiToken = streamingOptions.ApiToken;
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
string[] uploadExtensions = [".mp3", ".m4a", ".flac", ".wav", ".ogg", ".mp4", ".wmv"];

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
    var publicBaseUrl = streamingOptions.PublicBaseUrl;
    var baseUrl = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out _) ? publicBaseUrl : $"{request.Scheme}://{request.Host}";
    return $"{baseUrl}/music/{encoded}";
}

// The first Accept-Language entry decides the language of server-rendered pages and
// API error messages: Dutch when it is "nl", anything else falls back to English.
static bool PrefersDutch(HttpRequest request) =>
    request.Headers.AcceptLanguage.ToString().Split(',')[0].Trim()
        .StartsWith("nl", StringComparison.OrdinalIgnoreCase);

static string L(HttpRequest request, string nl, string en) => PrefersDutch(request) ? nl : en;

var auditStorage = app.Services.GetRequiredService<IAuditStorage>();
await auditStorage.InitializeAsync();
foreach (var track in EnumerateTracks())
    await auditStorage.IndexTrackAsync(Path.Combine(musicRoot, track), null, null);

app.MapGet("/api/tracks", async (HttpRequest request) =>
{
    var genres = await auditStorage.GetEffectiveGenresAsync(request.HttpContext.RequestAborted);
    var metadata = await auditStorage.GetTrackMetadataAsync(request.HttpContext.RequestAborted);
    return EnumerateTracks().Select(t => new TrackResponse(
        t,
        Path.GetFileNameWithoutExtension(t),
        UrlFor(request, t),
        genres.GetValueOrDefault(t, "onbekend"),
        metadata.GetValueOrDefault(t, new TrackMetadata("", "", 0, "", "", "", "", null, null, null, null)))).ToArray();
});

app.MapPost("/api/tracks/genre", async (GenreUpdateRequest request, HttpRequest http) =>
{
    var trackId = await auditStorage.FindTrackIdByRelativePathAsync(request.Path);
    if (trackId is null)
        return Results.NotFound(new ErrorResponse(L(http, "Track niet gevonden", "Track not found")));

    var genre = string.IsNullOrWhiteSpace(request.Genre) ? null : request.Genre.Trim();
    await auditStorage.SetGenreOverrideAsync(trackId.Value, genre);
    return Results.Ok();
});

app.MapPost("/api/tracks", async (HttpRequest request, AudioConverter converter, bool force = false) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new ErrorResponse(L(request, "Geen upload gevonden", "No upload found")));

    var form = await request.ReadFormAsync();
    List<string> saved = [];
    List<string> skipped = [];
    List<string> flagged = [];
    List<string> failed = [];
    var genre = form.TryGetValue("genre", out var genreValue) && !string.IsNullOrWhiteSpace(genreValue) ? genreValue.ToString().Trim() : null;
    var invoiceFile = form.Files.FirstOrDefault(candidate => candidate.Name == "invoice");
    long? purchaseEvidenceId = null;
    if (invoiceFile?.Length > 0)
    {
        using MemoryStream invoiceStream = new ();
        await invoiceFile.CopyToAsync(invoiceStream);
        purchaseEvidenceId = await auditStorage.StorePurchaseEvidenceAsync(
            new UploadedDocument(invoiceFile.FileName, invoiceFile.ContentType, invoiceStream.ToArray()));
    }

    foreach (var file in form.Files.Where(candidate => candidate.Name == "files"))
    {
        var name = file.FileName;
        var extension = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(name) || !uploadExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            skipped.Add(file.FileName);
            continue;
        }

        // make sure filename does not contain any directory components.
        if (!Path.GetFileName(name).Equals(name, StringComparison.Ordinal))
        {
            skipped.Add(file.FileName);
            continue;
        }

        var inputPath = Path.Combine(Path.GetTempPath(), $"streaming-{Guid.NewGuid():N}{extension}");
        var outputPath = Path.Combine(musicRoot, $".streaming-upload-{Guid.NewGuid():N}.tmp");
        var inputCreated = false;
        var outputCreated = false;
        try
        {
            await using (var stream = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                inputCreated = true;
                await file.CopyToAsync(stream, request.HttpContext.RequestAborted);
            }

            // Check the source before conversion, since not all tags survive transcoding.
            var reason = CopyrightCheck.Inspect(inputPath);
            if (reason is not null && !force)
            {
                flagged.Add($"{name}: {reason}");
                continue;
            }

            try
            {
                using (new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    outputCreated = true;
                await converter.ConvertAsync(inputPath, outputPath, request.HttpContext.RequestAborted);
            }
            catch (AudioConversionException ex)
            {
                app.Logger.LogWarning(ex, "Unable to convert uploaded file {FileName}", name);
                failed.Add($"{name}: {L(request, "Conversie mislukt; controleer het bestand en de FFmpeg-installatie", "Conversion failed; check the file and FFmpeg installation")}");
                continue;
            }

            var licenseFile = form.Files.FirstOrDefault(candidate => candidate.Name == $"license:{file.FileName}" && candidate.Length > 0);
            UploadedDocument? license = null;
            if (licenseFile is not null)
            {
                using var licenseStream = new MemoryStream();
                await licenseFile.CopyToAsync(licenseStream, request.HttpContext.RequestAborted);
                license = new UploadedDocument(licenseFile.FileName, licenseFile.ContentType, licenseStream.ToArray());
            }

            // The final move is atomic and never replaces an existing track.
            var stem = Path.GetFileNameWithoutExtension(name);
            var suffix = 1;
            string destination;
            while (true)
            {
                var outputName = suffix == 1 ? $"{stem}.mp3" : $"{stem} ({suffix}).mp3";
                destination = Path.Combine(musicRoot, outputName);
                try
                {
                    File.Move(outputPath, destination);
                    break;
                }
                catch (IOException) when (File.Exists(destination))
                {
                    suffix++;
                }
            }

            var trackId = await auditStorage.IndexTrackAsync(destination, purchaseEvidenceId, license, request.HttpContext.RequestAborted);
            if (genre is not null)
                await auditStorage.SetGenreOverrideAsync(trackId, genre, request.HttpContext.RequestAborted);

            saved.Add(Path.GetFileName(destination));
        }
        finally
        {
            if (inputCreated)
                File.Delete(inputPath);
            if (outputCreated)
                File.Delete(outputPath);
        }
    }

    return Results.Ok(new UploadResponse(saved, skipped, flagged, failed));
}).DisableAntiforgery();

app.MapDelete("/api/tracks/{*path}", (string path, HttpRequest request) =>
{
    var full = Path.GetFullPath(Path.Combine(musicRoot, path));
    if (!full.StartsWith(Path.GetFullPath(musicRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return Results.BadRequest(new ErrorResponse(L(request, "Ongeldig pad", "Invalid path")));

    if (!audioExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
        return Results.BadRequest(new ErrorResponse(L(request, "Geen muziekbestand", "Not a music file")));

    if (!File.Exists(full))
        return Results.NotFound(new ErrorResponse(L(request, "Bestand bestaat niet", "File does not exist")));

    File.Delete(full);
    var checksumPath = full + ".checksum";
    if (File.Exists(checksumPath))
        File.Delete(checksumPath);
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
    var title = L(request, "Muziek", "Music");
    var sb = new StringBuilder($"<!doctype html><html lang=\"{L(request, "nl", "en")}\"><meta charset=\"utf-8\"><title>{title}</title><h1>{title}</h1>");
    sb.Append($"<p><a href=\"/playlist.m3u\">playlist.m3u</a> &middot; <a href=\"/api/tracks\">api/tracks</a> &middot; <a href=\"/report\">{L(request, "Rapport (PDF)", "Report (PDF)")}</a></p><ul>");
    foreach (var track in EnumerateTracks())
    {
        var url = UrlFor(request, track);
        sb.Append("<li><a href=\"").Append(url).Append("\">")
          .Append(WebUtility.HtmlEncode(track)).Append("</a></li>");
    }
    sb.Append("</ul>");
    return Results.Content(sb.ToString(), "text/html", Encoding.UTF8);
});

app.MapGet("/report", (HttpRequest request) => Results.Content($$"""
    <!doctype html><html lang="{{L(request, "nl", "en")}}"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{L(request, "Rapporten", "Reports")}}</title><style>body{font-family:system-ui,sans-serif;margin:2rem;max-width:42rem}a{display:inline-block;margin:.5rem .5rem .5rem 0;padding:.7rem 1rem;border:1px solid #2d7d46;border-radius:.4rem;color:#2d7d46;text-decoration:none;font-weight:600}a:hover{background:#eaf5ed}</style>
    <p><a href="/">{{L(request, "Terug naar muziekbediening", "Back to music controls")}}</a></p><h1>{{L(request, "Rapporten", "Reports")}}</h1><a href="/report/music">{{L(request, "Muziekcollectie", "Music collection")}}</a><a href="/report/audit">{{L(request, "Speel Audit", "Play Audit")}}</a>
    """, "text/html", Encoding.UTF8));

app.MapGet("/schedule", () => Results.File(Path.Combine(app.Environment.WebRootPath, "schedule.html"), "text/html"));

app.MapGet("/api/schedule", async (IGenreScheduleService schedule, CancellationToken ct) =>
    Results.Ok(await schedule.LoadAsync(ct)));

app.MapPut("/api/schedule", async (ScheduleSaveRequest request, IGenreScheduleService schedule, CancellationToken ct) =>
{
    await schedule.SaveAsync(request, ct);
    return Results.Ok(await schedule.LoadAsync(ct));
});

app.MapGet("/report/music", (HttpRequest request, IMusicReportService reportService) =>
{
    if (request.Query.ContainsKey("download") || request.Query["format"] == "pdf" || request.Headers.Accept.ToString().Contains(PdfDocument.PdfContentType))
    {
        var pdf = reportService.GeneratePdfReport();
        var download = request.Query.ContainsKey("download") && request.Query["download"] != "false";
        return Results.File(pdf, PdfDocument.PdfContentType, fileDownloadName: download ? "muziek-rapport.pdf" : null);
    }

    var data = reportService.CollectReportData();
    var hourSuffix = L(request, "u", "h");
    var durationFormatted = data.TotalDuration.TotalHours >= 1
        ? $"{(int)data.TotalDuration.TotalHours}{hourSuffix} {data.TotalDuration.Minutes:D2}m {data.TotalDuration.Seconds:D2}s"
        : $"{data.TotalDuration.Minutes:D2}m {data.TotalDuration.Seconds:D2}s";

    var sizeMb = data.TotalSizeBytes / (1024f * 1024f);
    var sizeFormatted = sizeMb >= 1000f ? $"{(sizeMb / 1024f):0.##} GB" : $"{sizeMb:0.#} MB";

    var sb = new StringBuilder();
    sb.Append($$"""
        <!doctype html>
        <html lang="{{L(request, "nl", "en")}}">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{L(request, "Muziekcollectie Rapport", "Music Collection Report")}}</title>
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
          <a href="/">&larr; {{L(request, "Terug naar Muziekbediening", "Back to music controls")}}</a> &middot; <a href="/tracks">{{L(request, "Lijstweergave", "List view")}}</a>
        </div>
        <h1>{{L(request, "Muziekcollectie Rapport", "Music Collection Report")}}</h1>

        <div class="stats-grid">
          <div class="stat-card"><div class="label">{{L(request, "Nummers", "Tracks")}}</div><div class="val">{{data.Tracks.Count}}</div></div>
          <div class="stat-card"><div class="label">{{L(request, "Speelduur", "Playing time")}}</div><div class="val">{{durationFormatted}}</div></div>
          <div class="stat-card"><div class="label">{{L(request, "Grootte", "Size")}}</div><div class="val">{{sizeFormatted}}</div></div>
          <div class="stat-card"><div class="label">{{L(request, "Vrij / CC", "Free / CC")}}</div><div class="val" style="color: #2d7d46;">{{data.FreeCount}}</div></div>
          <div class="stat-card"><div class="label">{{L(request, "Beschermd", "Protected")}}</div><div class="val" style="color: #dc2626;">{{data.ProtectedCount}}</div></div>
          <div class="stat-card"><div class="label">{{L(request, "Geen tags", "No tags")}}</div><div class="val">{{data.UnspecifiedCount}}</div></div>
        </div>

        <div class="actions">
          <a href="/report/pdf?download=true" class="btn primary">📥 Download PDF</a>
          <a href="/report/pdf" target="_blank" class="btn">📄 {{L(request, "Open PDF in nieuw tabblad", "Open PDF in new tab")}}</a>
        </div>

        <div class="email-box">
          <h2>✉️ {{L(request, "Rapport per E-mail versturen", "Send report by email")}}</h2>
          <form id="emailForm" onsubmit="event.preventDefault(); sendEmail();">
            <div class="email-row">
              <input type="email" id="emailAddress" placeholder="{{L(request, "naam@voorbeeld.nl", "name@example.com")}}" required>
              <button type="submit" id="btnSendEmail" class="btn primary">{{L(request, "Verstuur PDF", "Send PDF")}}</button>
            </div>
            <div id="emailStatus"></div>
          </form>
        </div>

        <div class="preview-section">
          <h2>{{L(request, "Voorvertoning van PDF", "PDF preview")}}</h2>
          <iframe src="/report/pdf" title="{{L(request, "PDF Rapport Voorvertoning", "PDF report preview")}}"></iframe>
        </div>

        <script>
        async function sendEmail() {
          const emailInput = document.getElementById('emailAddress');
          const statusEl = document.getElementById('emailStatus');
          const btn = document.getElementById('btnSendEmail');
          const email = emailInput.value.trim();

          if (!email) {
            statusEl.textContent = '{{L(request, "Vul een geldig e-mailadres in.", "Enter a valid email address.")}}';
            statusEl.style.color = '#ef4444';
            return;
          }

          btn.disabled = true;
          statusEl.textContent = '{{L(request, "Bezig met versturen...", "Sending...")}}';
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
              statusEl.textContent = '✗ ' + (data.error || data.message || '{{L(request, "Verzenden mislukt", "Sending failed")}}');
              statusEl.style.color = '#ef4444';
            }
          } catch (err) {
            statusEl.textContent = '✗ {{L(request, "Fout", "Error")}}: ' + err.message;
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

app.MapGet("/report/audit", (HttpRequest request) => Results.Content($$"""
    <!doctype html><html lang="{{L(request, "nl", "en")}}"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{L(request, "Speel Audit", "Play Audit")}}</title><style>body{font-family:system-ui,sans-serif;margin:2rem;max-width:42rem}.btn,input{font:inherit;padding:.6rem .8rem;border:1px solid #777;border-radius:.4rem}.btn{color:#fff;background:#2d7d46;border-color:#2d7d46;cursor:pointer;text-decoration:none;display:inline-block}form{margin-top:1rem;display:flex;gap:.5rem;flex-wrap:wrap}#status{min-height:1.4rem}</style>
    <p><a href="/report">{{L(request, "Terug naar rapporten", "Back to reports")}}</a></p><h1>{{L(request, "Speel Audit", "Play Audit")}}</h1><p><a class="btn" href="/report/audit/download">{{L(request, "Download ZIP met JSON", "Download ZIP with JSON")}}</a></p><form id="form"><input id="email" type="email" placeholder="{{L(request, "naam@voorbeeld.nl", "name@example.com")}}" required><button class="btn">{{L(request, "Verstuur ZIP per e-mail", "Send ZIP by email")}}</button></form><p id="status"></p>
    <script>document.getElementById('form').onsubmit=async e=>{e.preventDefault();const s=document.getElementById('status');const token=localStorage.getItem('apiToken');const r=await fetch('/report/audit/email',{method:'POST',headers:{'Content-Type':'application/json',...(token?{'X-Api-Token':token}:{})},body:JSON.stringify({email:document.getElementById('email').value})});const d=await r.json();s.textContent=d.message||d.error||'{{L(request, "Verzenden mislukt", "Sending failed")}}';};</script>
    """, "text/html", Encoding.UTF8));

app.MapGet("/report/pdf", (IMusicReportService reportService, bool download = false) =>
{
    var pdf = reportService.GeneratePdfReport();
    return Results.File(pdf, PdfDocument.PdfContentType, fileDownloadName: download ? "muziek-rapport.pdf" : null);
});

app.MapGet("/report/download", (IMusicReportService reportService) =>
{
    var pdf = reportService.GeneratePdfReport();
    return Results.File(pdf, PdfDocument.PdfContentType, fileDownloadName: "muziek-rapport.pdf");
});

app.MapGet("/report/audit/download", async (IPlayAuditReportService reportService, CancellationToken ct) =>
{
    var zipStream = await reportService.GenerateZipAsync(ct);
    return Results.Stream(zipStream, "application/zip", "speel-audit.zip");
});

app.MapPost("/report/audit/email", async (EmailReportRequest request, HttpRequest http, IEmailReportService emailService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.BadRequest(new EmailReportResponse(false, L(http, "E-mailadres ontbreekt", "Email address is missing")));
    try
    {
        await emailService.SendPlayAuditReportAsync(request.Email, ct);
        return Results.Ok(new EmailReportResponse(true, L(http,
            $"Het speelauditrapport is succesvol gemaild naar {request.Email}",
            $"The play audit report was emailed to {request.Email}")));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new EmailReportResponse(false, ex.Message));
    }
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
        return Results.BadRequest(new EmailReportResponse(false, L(request, "E-mailadres ontbreekt", "Email address is missing")));

    try
    {
        await emailService.SendReportAsync(email, ct);
        return Results.Ok(new EmailReportResponse(true, L(request,
            $"Het rapport is succesvol gemaild naar {email}",
            $"The report was emailed to {email}")));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new EmailReportResponse(false, ex.Message));
    }
});

app.MapPost("/api/report/email", async (EmailReportRequest request, HttpRequest http, IEmailReportService emailService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.BadRequest(new EmailReportResponse(false, L(http, "E-mailadres ontbreekt", "Email address is missing")));

    try
    {
        await emailService.SendReportAsync(request.Email, ct);
        return Results.Ok(new EmailReportResponse(true, L(http,
            $"Het rapport is succesvol gemaild naar {request.Email}",
            $"The report was emailed to {request.Email}")));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new EmailReportResponse(false, ex.Message));
    }
});

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", EnumerateTracks().Count())));

bool UseSonosPlayback() => app.Configuration.GetValue("Streaming:Playback:UseSonos", true);

app.MapGet("/api/speakers", (SonosService sonos, bool refresh = false) =>
    UseSonosPlayback() ? sonos.DiscoverAsync(refresh) : Task.FromResult<IReadOnlyList<SonosSpeaker>>([new SonosSpeaker("USB-audio/DAC", "local", "broadcast")]));

app.MapGet("/api/state", async (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker) =>
{
    if (!UseSonosPlayback())
    {
        var broadcastState = broadcast.GetState();
        return Results.Ok(new StateResponse("USB-audio/DAC", broadcastState.State, broadcastState.Title, broadcastState.ElapsedSeconds, broadcastState.DurationSeconds));
    }

    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse(L(request, "Geen speaker gevonden", "No speaker found")));

    var state = await sonos.GetTransportStateAsync(target);
    var position = await sonos.GetPositionInfoAsync(target);
    return Results.Ok(new StateResponse(
        target.Name,
        state,
        position?.Title,
        position?.Elapsed.TotalSeconds,
        position?.Duration.TotalSeconds));
});

app.MapPost("/api/play", async (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker, bool shuffle = false) =>
{
    var schedule = request.HttpContext.RequestServices.GetRequiredService<IGenreScheduleService>();
    var (tracks, filteredBySchedule) = await EnumeratePlayableTracksAsync(schedule, request.HttpContext.RequestAborted);
    if (tracks.Length == 0)
        return Results.BadRequest(new ErrorResponse(filteredBySchedule
            ? L(request, "Geen muziek gevonden voor huidig schema", "No music found for the current schedule")
            : L(request, "Geen muziek gevonden", "No music found")));

    if (!UseSonosPlayback())
    {
        await broadcast.StartAsync(tracks, shuffle, request.HttpContext.RequestAborted);
        return Results.Ok(new PlayResponse("USB-audio/DAC", tracks.Length));
    }

    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse(L(request, "Geen speaker gevonden", "No speaker found")));

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

async Task<(string[] Tracks, bool FilteredBySchedule)> EnumeratePlayableTracksAsync(IGenreScheduleService schedule, CancellationToken cancellationToken)
{
    var tracks = EnumerateTracks().ToArray();
    var activeGenres = await schedule.GetActiveGenresAsync(DateTimeOffset.Now, cancellationToken);
    if (activeGenres is null)
        return (tracks, false);

    var effectiveGenres = await auditStorage.GetEffectiveGenresAsync(cancellationToken);
    return (tracks.Where(t => GenreScheduleService.TrackMatchesGenres(effectiveGenres.GetValueOrDefault(t, "onbekend"), activeGenres)).ToArray(), true);
}

app.MapPost("/api/pause", (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker) => PlaybackCommand(sonos, broadcast, request, speaker, s => sonos.PauseAsync(s), b => b.PauseAsync()));
app.MapPost("/api/stop", (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker) => PlaybackCommand(sonos, broadcast, request, speaker, s => sonos.StopAsync(s), b => b.StopAsync()));
app.MapPost("/api/next", (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker) => PlaybackCommand(sonos, broadcast, request, speaker, s => sonos.NextAsync(s), b => b.NextAsync()));
app.MapPost("/api/previous", (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker) => PlaybackCommand(sonos, broadcast, request, speaker, s => sonos.PreviousAsync(s), b => b.PreviousAsync()));
app.MapPost("/api/volume", (SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, int level, string? speaker) => PlaybackCommand(sonos, broadcast, request, speaker, s => sonos.SetVolumeAsync(s, level), b => { b.SetVolume(level); return Task.CompletedTask; }));

async Task<IResult> PlaybackCommand(SonosService sonos, IBroadcastPlaybackService broadcast, HttpRequest request, string? speaker, Func<SonosSpeaker, Task> sonosAction, Func<IBroadcastPlaybackService, Task> broadcastAction)
{
    if (!UseSonosPlayback())
    {
        await broadcastAction(broadcast);
        return Results.Ok(new SpeakerResponse("USB-audio/DAC"));
    }

    return await Command(sonos, request, speaker, sonosAction);
}

async Task<IResult> Command(SonosService sonos, HttpRequest request, string? speaker, Func<SonosSpeaker, Task> action)
{
    var target = await sonos.ResolveAsync(speaker);
    if (target is null)
        return Results.NotFound(new ErrorResponse(L(request, "Geen speaker gevonden", "No speaker found")));

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
