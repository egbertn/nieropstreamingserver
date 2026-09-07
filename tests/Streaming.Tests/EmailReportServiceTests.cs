using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Streaming.Tests;

public sealed class EmailReportServiceTests
{
    private sealed class MockSmtpClientWrapper : ISmtpClientWrapper
    {
        public string? ConnectedHost { get; private set; }
        public int ConnectedPort { get; private set; }
        public SecureSocketOptions ConnectedOptions { get; private set; }
        public string? AuthenticatedUsername { get; private set; }
        public string? AuthenticatedPassword { get; private set; }
        public MimeMessage? SentMessage { get; private set; }
        public bool IsDisconnected { get; private set; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            ConnectedHost = host;
            ConnectedPort = port;
            ConnectedOptions = options;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            AuthenticatedUsername = username;
            AuthenticatedPassword = password;
            return Task.CompletedTask;
        }

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
        {
            SentMessage = message;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            IsDisconnected = true;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class DummyMusicReportService : IMusicReportService
    {
        public MusicReportData CollectReportData()
        {
            var item = new TrackReportItem(
                "track1.mp3",
                "track1.mp3",
                5000000,
                DateTime.UtcNow,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(4),
                "Test Track",
                "Test Artist",
                "Test Album",
                2024,
                "Test Genre",
                320,
                "MP3",
                new TrackCopyrightDetails(
                    TrackCopyrightStatus.Free,
                    "FREE",
                    "Geen copyright restricties",
                    null,
                    null,
                    null,
                    "public domain"
                )
            );

            return new MusicReportData(
                DateTime.UtcNow,
                [item],
                TimeSpan.FromMinutes(4),
                5000000,
                1,
                0,
                0
            );
        }

        public byte[] GeneratePdfReport(MusicReportData? data = null)
        {
            return "%PDF-1.4 mock pdf data %%EOF"u8.ToArray();
        }
    }

    [Fact]
    public void CreateReportEmailMessage_BuildsValidMimeMessageWithPdfAttachment()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Streaming:Smtp:Host"] = "smtp.example.com",
                ["Streaming:Smtp:From"] = "sender@example.com",
                ["Streaming:Smtp:FromName"] = "Muziek Server"
            })
            .Build();

        var reportService = new DummyMusicReportService();
        var emailService = new EmailReportService(config, reportService, NullLogger<EmailReportService>.Instance);

        var data = reportService.CollectReportData();
        var pdfBytes = reportService.GeneratePdfReport(data);

        var message = emailService.CreateReportEmailMessage("recipient@example.com", data, pdfBytes);

        Assert.NotNull(message);
        Assert.Equal("sender@example.com", message.From.Mailboxes.First().Address);
        Assert.Equal("Muziek Server", message.From.Mailboxes.First().Name);
        Assert.Equal("recipient@example.com", message.To.Mailboxes.First().Address);
        Assert.Contains("Muziekcollectie Rapport", message.Subject);

        // Verify attachment is present
        var attachment = message.Attachments.FirstOrDefault();
        Assert.NotNull(attachment);
        Assert.Equal("muziek-rapport.pdf", ((MimePart)attachment).FileName);
        Assert.Equal(PdfDocument.PdfContentType, attachment.ContentType.MimeType);
    }

    [Fact]
    public async Task SendReportAsync_ValidInput_ConnectsAndSendsViaSmtp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Streaming:Smtp:Host"] = "mail.myhost.com",
                ["Streaming:Smtp:Port"] = "587",
                ["Streaming:Smtp:Username"] = "smtpuser",
                ["Streaming:Smtp:Password"] = "smtppass",
                ["Streaming:Smtp:From"] = "music@myhost.com"
            })
            .Build();

        var mockWrapper = new MockSmtpClientWrapper();
        var reportService = new DummyMusicReportService();
        var emailService = new EmailReportService(
            config,
            reportService,
            NullLogger<EmailReportService>.Instance,
            () => mockWrapper);

        await emailService.SendReportAsync("target@receiver.com");

        Assert.Equal("mail.myhost.com", mockWrapper.ConnectedHost);
        Assert.Equal(587, mockWrapper.ConnectedPort);
        Assert.Equal("smtpuser", mockWrapper.AuthenticatedUsername);
        Assert.Equal("smtppass", mockWrapper.AuthenticatedPassword);
        Assert.NotNull(mockWrapper.SentMessage);
        Assert.Equal("target@receiver.com", mockWrapper.SentMessage.To.Mailboxes.First().Address);
        Assert.True(mockWrapper.IsDisconnected);
    }

    [Fact]
    public async Task SendReportAsync_InvalidEmail_ThrowsArgumentException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Streaming:Smtp:Host"] = "mail.test.com"
            })
            .Build();

        var emailService = new EmailReportService(config, new DummyMusicReportService(), NullLogger<EmailReportService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => emailService.SendReportAsync("invalid-email-address"));
    }

    [Fact]
    public async Task SendReportAsync_MissingHostConfig_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder().Build();
        var emailService = new EmailReportService(config, new DummyMusicReportService(), NullLogger<EmailReportService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => emailService.SendReportAsync("user@example.com"));
    }
}
