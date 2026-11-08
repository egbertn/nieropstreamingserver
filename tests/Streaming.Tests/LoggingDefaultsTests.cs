using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Streaming.Tests;

public sealed class LoggingDefaultsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("Trace", true)]
    [InlineData("Information", true)]
    public async Task SonosHttpLogging_UsesQuietDefaultAndHonorsExplicitCategoryLevel(
        string? configuredLevel, bool expectSonosInformation)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            [$"Logging:LogLevel:{LoggingDefaults.SonosHttpClientCategory}"] = configuredLevel
        }.Where(entry => entry.Value is not null));
        LoggingDefaults.Apply(builder.Configuration);

        var logs = new RecordingLoggerProvider();
        builder.Logging.AddProvider(logs);
        builder.Services.AddHttpClient(nameof(SonosService))
            .ConfigurePrimaryHttpMessageHandler(() => new SuccessfulHandler());
        builder.Services.AddHttpClient("Other")
            .ConfigurePrimaryHttpMessageHandler(() => new SuccessfulHandler());

        using var host = builder.Build();
        var clients = host.Services.GetRequiredService<IHttpClientFactory>();
        await clients.CreateClient(nameof(SonosService)).GetAsync("http://example.test/");
        await clients.CreateClient("Other").GetAsync("http://example.test/");
        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger($"{LoggingDefaults.SonosHttpClientCategory}.ClientHandler")
            .LogWarning("Test warning");

        foreach (var handler in new[] { "LogicalHandler", "ClientHandler" })
            Assert.Equal(expectSonosInformation, logs.Entries.Any(entry =>
                entry.Category == $"{LoggingDefaults.SonosHttpClientCategory}.{handler}"
                && entry.Level == LogLevel.Information));
        Assert.Contains(logs.Entries, entry =>
            entry.Category == $"{LoggingDefaults.SonosHttpClientCategory}.ClientHandler"
            && entry.Level == LogLevel.Warning);
        Assert.Contains(logs.Entries, entry =>
            entry.Category.StartsWith("System.Net.Http.HttpClient.Other", StringComparison.Ordinal)
            && entry.Level == LogLevel.Information);
    }

    private sealed class SuccessfulHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<(string Category, LogLevel Level)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel));
    }
}
