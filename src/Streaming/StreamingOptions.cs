namespace Streaming;

/// <summary>
/// Resolved application settings, computed once at startup so services no longer need
/// direct access to <see cref="IConfiguration"/> or <see cref="IWebHostEnvironment"/>.
/// </summary>
public sealed class StreamingOptions
{
    public required string MusicRoot { get; init; }
    public required string DatabasePath { get; init; }
    public string SchedulePath { get; init; } = Path.Combine("data", "schedule.json");
    public string? ApiToken { get; init; }
    public string? PublicBaseUrl { get; init; }
    public int Port { get; init; } = 8080;
    public string FfplayPath { get; init; } = "ffplay";
    public bool UseSonos { get; init; } = true;
    public string[] SonosAddresses { get; init; } = [];
    public int SonosDiscoveryPort { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public required string SmtpUsername { get; init; }
    public required string SmtpPassword { get; init; }
    public bool SmtpUseSsl { get; init; }
    public string SmtpFrom { get; init; } = "music-report@local";
    public string SmtpFromName { get; init; } = "Sonos Muziek Server";

    public static StreamingOptions FromConfiguration(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var databasePath = configuration.GetConnectionString("sqlite");
        if (string.IsNullOrWhiteSpace(databasePath))
            databasePath = Path.Combine(environment.ContentRootPath, "data", "streaming.db");
        else if (!Path.IsPathRooted(databasePath))
            databasePath = Path.Combine(environment.ContentRootPath, databasePath);

        var schedulePath = configuration["Streaming:SchedulePath"];
        if (string.IsNullOrWhiteSpace(schedulePath))
            schedulePath = Path.Combine(environment.ContentRootPath, "data", "schedule.json");
        else if (!Path.IsPathRooted(schedulePath))
            schedulePath = Path.Combine(environment.ContentRootPath, schedulePath);

        var addresses = configuration.GetSection("Streaming:Sonos:Addresses");
        var sonosAddresses = addresses.Value is { } addressList
            ? addressList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : addresses.GetChildren()
                .Select(child => child.Value)
                .OfType<string>()
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .ToArray();

        return new StreamingOptions
        {
            MusicRoot = Path.Combine(environment.ContentRootPath, "wwwroot", "music"),
            DatabasePath = databasePath,
            SchedulePath = schedulePath,
            ApiToken = configuration["Streaming:ApiToken"],
            PublicBaseUrl = configuration["Streaming:PublicBaseUrl"]?.TrimEnd('/'),
            Port = configuration.GetValue("Streaming:Port", 8080),
            FfplayPath = configuration["Streaming:Playback:FfplayPath"] ?? "ffplay",
            UseSonos = configuration.GetValue("Streaming:Playback:UseSonos", true),
            SonosAddresses = sonosAddresses,
            SonosDiscoveryPort = configuration.GetValue("Streaming:Sonos:DiscoveryPort", 0),
            SmtpHost = configuration["Smtp:Host"],
            SmtpPort = configuration.GetValue("Smtp:Port", 587),
            SmtpUsername = configuration["Smtp:Username"],
            SmtpPassword = configuration["Smtp:Password"],
            SmtpUseSsl = configuration.GetValue("Smtp:UseSsl", false),
            SmtpFrom =  configuration["Smtp:From"] ?? "music-report@local",
            SmtpFromName = configuration["Smtp:FromName"] ?? "Sonos Muziek Server",
        };
    }
}
