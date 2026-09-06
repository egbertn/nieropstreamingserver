using System.Globalization;

using MailKit.Security;
using MimeKit;

namespace Streaming;

public interface ISmtpClientWrapper : IDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default);
    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
    Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default);
}

public sealed class DefaultSmtpClientWrapper : ISmtpClientWrapper
{
    private readonly MailKit.Net.Smtp.SmtpClient _client = new();

    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(host, port, options, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default) =>
        _client.AuthenticateAsync(username, password, cancellationToken);

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) =>
        _client.SendAsync(message, cancellationToken);

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) =>
        _client.DisconnectAsync(quit, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public interface IEmailReportService
{
    Task SendReportAsync(string recipientEmail, CancellationToken cancellationToken = default);
    MimeMessage CreateReportEmailMessage(string recipientEmail, MusicReportData data, byte[] pdfBytes);
}

public sealed class EmailReportService : IEmailReportService
{
    private const string PdfContentType = "application/pdf";
    private readonly IConfiguration _configuration;
    private readonly IMusicReportService _reportService;
    private readonly ILogger<EmailReportService> _logger;
    private readonly Func<ISmtpClientWrapper> _smtpClientFactory;

    public EmailReportService(
        IConfiguration configuration,
        IMusicReportService reportService,
        ILogger<EmailReportService> logger,
        Func<ISmtpClientWrapper>? smtpClientFactory = null)
    {
        _configuration = configuration;
        _reportService = reportService;
        _logger = logger;
        _smtpClientFactory = smtpClientFactory ?? (() => new DefaultSmtpClientWrapper());
    }

    public async Task SendReportAsync(string recipientEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail) || !IsValidEmail(recipientEmail))
        {
            throw new ArgumentException("Ongeldig e-mailadres opgegeven.", nameof(recipientEmail));
        }

        var host = _configuration["Streaming:Smtp:Host"] ?? _configuration["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP-server is niet geconfigureerd in appsettings.json (Streaming:Smtp:Host).");
        }

        var port = _configuration.GetValue("Streaming:Smtp:Port", _configuration.GetValue("Smtp:Port", 587));
        var username = _configuration["Streaming:Smtp:Username"] ?? _configuration["Smtp:Username"];
        var password = _configuration["Streaming:Smtp:Password"] ?? _configuration["Smtp:Password"];
        var useSsl = _configuration.GetValue("Streaming:Smtp:UseSsl", _configuration.GetValue("Smtp:UseSsl", false));

        var data = _reportService.CollectReportData();
        var pdfBytes = _reportService.GeneratePdfReport(data);

        var message = CreateReportEmailMessage(recipientEmail, data, pdfBytes);

        _logger.LogInformation("Versturen van muziekrapport PDF naar {Recipient} via {Host}:{Port}", recipientEmail, host, port);

        using var client = _smtpClientFactory();
        var secureOptions = useSsl
            ? (port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(host, port, secureOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(username))
        {
            await client.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("Muziekrapport succesvol verzonden naar {Recipient}", recipientEmail);
    }

    public MimeMessage CreateReportEmailMessage(string recipientEmail, MusicReportData data, byte[] pdfBytes)
    {
        var fromAddress = _configuration["Streaming:Smtp:From"] ?? _configuration["Smtp:From"] ?? "music-report@local";
        var fromName = _configuration["Streaming:Smtp:FromName"] ?? _configuration["Smtp:FromName"] ?? "Sonos Muziek Server";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = $"Muziekcollectie Rapport ({data.Tracks.Count} nummers) - {DateTime.UtcNow.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}";

        var bodyBuilder = new BodyBuilder();

        var totalHours = (int)data.TotalDuration.TotalHours;
        var durationText = FormatDuration(data.TotalDuration, totalHours);

        var sizeMb = data.TotalSizeBytes / (1024f * 1024f);
        var sizeText = FormatSize(sizeMb);

        bodyBuilder.TextBody = $"""
            Beste,

            In de bijlage treft u het actuele overzicht van de muziekcollectie aan.

            Samenvatting:
            - Aantal nummers: {data.Tracks.Count}
            - Totale speelduur: {durationText}
            - Totale bestandsgrootte: {sizeText}
            - Copyright status: {data.FreeCount} vrij / {data.ProtectedCount} beschermd / {data.UnspecifiedCount} geen vermelding
            - Gegenereerd op: {data.GeneratedAtUtc.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture)} UTC

            Zie het bijgevoegde PDF-document (muziek-rapport.pdf) voor de volledige details per bestand inclusief tags, duur en copyright-analyse.

            Met vriendelijke groet,
            {fromName}
            """;

        bodyBuilder.HtmlBody = $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; color: #1e293b; line-height: 1.5; }
                .card { max-width: 580px; margin: 20px auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden; }
                .header { background: #2d7d46; color: white; padding: 20px; }
                .header h1 { margin: 0; font-size: 20px; }
                .content { padding: 20px; background: #ffffff; }
                .stats { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 6px; padding: 12px 16px; margin: 16px 0; }
                .stats-table { width: 100%; border-collapse: collapse; }
                .stats-table td { padding: 6px 0; }
                .label { color: #64748b; font-size: 13px; }
                .value { font-weight: 600; font-size: 14px; text-align: right; }
                .footer { padding: 12px 20px; font-size: 12px; color: #94a3b8; background: #f8fafc; border-top: 1px solid #e2e8f0; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="header">
                  <h1>Muziekcollectie Rapport</h1>
                </div>
                <div class="content">
                  <p>In de bijlage treft u het PDF-overzicht aan van de aanwezige muziekbestanden op de server.</p>
                  <div class="stats">
                    <table class="stats-table">
                      <tr><td class="label">Aantal nummers</td><td class="value">{{data.Tracks.Count}}</td></tr>
                      <tr><td class="label">Totale speelduur</td><td class="value">{{durationText}}</td></tr>
                      <tr><td class="label">Totale omvang</td><td class="value">{{sizeText}}</td></tr>
                      <tr><td class="label">Copyright analyse</td><td class="value">{{data.FreeCount}} vrij &bull; {{data.ProtectedCount}} beschermd &bull; {{data.UnspecifiedCount}} onbekend</td></tr>
                      <tr><td class="label">Datum</td><td class="value">{{data.GeneratedAtUtc.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)}} UTC</td></tr>
                    </table>
                  </div>
                  <p>Open het bijgevoegde PDF-bestand <strong>muziek-rapport.pdf</strong> voor het volledige rapport met alle audiotags, bestandseigenschappen en licentie-analyses.</p>
                </div>
                <div class="footer">
                  Automatisch gegenereerd door {{fromName}}
                </div>
              </div>
            </body>
            </html>
            """;

        bodyBuilder.Attachments.Add("muziek-rapport.pdf", pdfBytes, new ContentType("application", "pdf"));
        message.Body = bodyBuilder.ToMessageBody();

        return message;
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return false;

        if (!MailboxAddress.TryParse(email, out var mailbox))
            return false;

        var atIndex = mailbox.Address.IndexOf('@');
        return atIndex > 0 && atIndex < mailbox.Address.Length - 1 && mailbox.Address.IndexOf('.', atIndex) > atIndex;
    }

    private static string FormatDuration(TimeSpan duration, int totalHours) =>
        totalHours > 0
            ? $"{totalHours}u {duration.Minutes:D2}m {duration.Seconds:D2}s"
            : $"{duration.Minutes:D2}m {duration.Seconds:D2}s";

    private static string FormatSize(float sizeMb) =>
        sizeMb >= 1000f
            ? $"{sizeMb / 1024f:0.##} GB"
            : $"{sizeMb:0.#} MB";
}
