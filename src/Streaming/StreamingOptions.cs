namespace Streaming;

/// <summary>
/// Resolved application settings, computed once at startup so services no longer need
/// direct access to <see cref="IConfiguration"/> or <see cref="IWebHostEnvironment"/>.
/// </summary>
public sealed class StreamingOptions
{
    public required string MusicRoot { get; init; }
    public required string DatabasePath { get; init; }
    public string? ApiToken { get; init; }
    public string? PublicBaseUrl { get; init; }
    public int Port { get; init; } = 8080;
    public string FfplayPath { get; init; } = "ffplay";
    public bool UseSonos { get; init; } = true;
    public string[] SonosAddresses { get; init; } = [];
    public int SonosDiscoveryPort { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public bool SmtpUseSsl { get; init; }
    public string SmtpFrom { get; init; } = "music-report@local";
    public string SmtpFromName { get; init; } = "Sonos Muziek Server";

    public static StreamingOptions FromConfiguration(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var databasePath = configuration["Streaming:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
            databasePath = Path.Combine(environment.ContentRootPath, "data", "streaming.db");
        else if (!Path.IsPathRooted(databasePath))
            databasePath = Path.Combine(environment.ContentRootPath, databasePath);

        return new StreamingOptions
        {
            MusicRoot = Path.Combine(environment.ContentRootPath, "wwwroot", "music"),
            DatabasePath = databasePath,
            ApiToken = configuration["Streaming:ApiToken"],
            PublicBaseUrl = configuration["Streaming:PublicBaseUrl"]?.TrimEnd('/'),
            Port = configuration.GetValue("Streaming:Port", 8080),
            FfplayPath = configuration["Streaming:Playback:FfplayPath"] ?? "ffplay",
            UseSonos = configuration.GetValue("Streaming:Playback:UseSonos", true),
            SonosAddresses = configuration.GetValue<string[]>("Streaming:Sonos:Addresses") ?? [],
            SonosDiscoveryPort = configuration.GetValue("Streaming:Sonos:DiscoveryPort", 0),
            SmtpHost = configuration["Streaming:Smtp:Host"] ?? configuration["Smtp:Host"],
            SmtpPort = configuration.GetValue("Streaming:Smtp:Port", configuration.GetValue("Smtp:Port", 587)),
            SmtpUsername = configuration["Streaming:Smtp:Username"] ?? configuration["Smtp:Username"],
            SmtpPassword = configuration["Streaming:Smtp:Password"] ?? configuration["Smtp:Password"],
            SmtpUseSsl = configuration.GetValue("Streaming:Smtp:UseSsl", configuration.GetValue("Smtp:UseSsl", false)),
            SmtpFrom = configuration["Streaming:Smtp:From"] ?? configuration["Smtp:From"] ?? "music-report@local",
            SmtpFromName = configuration["Streaming:Smtp:FromName"] ?? configuration["Smtp:FromName"] ?? "Sonos Muziek Server",
        };
    }
}
