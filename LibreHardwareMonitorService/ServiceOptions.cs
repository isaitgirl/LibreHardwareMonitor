using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace LibreHardwareMonitorService;

internal enum ServiceLogLevel
{
    Debug = 0,
    Info = 1
}

internal sealed class ServiceOptions
{
    public const string InstallDirectory = @"C:\LibreHardwareMonitorService";

    public static readonly string DefaultConfigJson = """
{
  "refreshInterval": "00:01:00",
  "logLevel": "INFO",
    "httpPort": 8088,
    "writeMetricsToFile": false,
    "writeMetricsInterval": "00:01:00"
}
""";

    public required TimeSpan RefreshInterval { get; init; }

    public required ServiceLogLevel LogLevel { get; init; }

    public required int HttpPort { get; init; }

    public required bool WriteMetricsToFile { get; init; }

    public required TimeSpan WriteMetricsInterval { get; init; }

    public static ServiceOptions FromConfiguration(IConfiguration configuration)
    {
        string? refreshIntervalValue = configuration["refreshInterval"];
        TimeSpan refreshInterval = ParseRefreshInterval(refreshIntervalValue);

        string logLevelValue = (configuration["logLevel"] ?? "INFO").Trim();
        ServiceLogLevel logLevel = logLevelValue.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
            ? ServiceLogLevel.Debug
            : ServiceLogLevel.Info;

        int httpPort = 8088;
        if (int.TryParse(configuration["httpPort"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int configuredPort) &&
            configuredPort is > 0 and <= 65535)
        {
            httpPort = configuredPort;
        }

        bool writeMetricsToFile = false;
        if (bool.TryParse(configuration["writeMetricsToFile"], out bool parsedWriteMetrics))
        {
            writeMetricsToFile = parsedWriteMetrics;
        }

        TimeSpan writeMetricsInterval = ParseInterval(configuration["writeMetricsInterval"], TimeSpan.FromSeconds(60));

        return new ServiceOptions
        {
            RefreshInterval = refreshInterval,
            LogLevel = logLevel,
            HttpPort = httpPort,
            WriteMetricsToFile = writeMetricsToFile,
            WriteMetricsInterval = writeMetricsInterval
        };
    }

    private static TimeSpan ParseRefreshInterval(string? rawValue)
    {
        return ParseInterval(rawValue, TimeSpan.FromMinutes(1));
    }

    private static TimeSpan ParseInterval(string? rawValue, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out TimeSpan parsedTimeSpan))
        {
            return parsedTimeSpan <= TimeSpan.Zero ? defaultValue : parsedTimeSpan;
        }

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeconds))
        {
            if (parsedSeconds <= 0)
                return defaultValue;

            return TimeSpan.FromSeconds(parsedSeconds);
        }

        return defaultValue;
    }
}