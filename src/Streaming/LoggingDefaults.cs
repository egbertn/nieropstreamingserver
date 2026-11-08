using Microsoft.Extensions.Configuration.Memory;

namespace Streaming;

internal static class LoggingDefaults
{
    internal const string SonosHttpClientCategory = "System.Net.Http.HttpClient.SonosService";

    internal static void Apply(ConfigurationManager configuration)
    {
        configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                [$"Logging:LogLevel:{SonosHttpClientCategory}"] = "Warning"
            }
        });
    }
}
