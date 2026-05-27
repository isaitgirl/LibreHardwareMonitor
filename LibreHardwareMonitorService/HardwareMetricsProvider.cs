using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Hosting;

namespace LibreHardwareMonitorService;

internal sealed class HardwareMetricsProvider : IHostedService, IDisposable
{
    private readonly object _computerLock = new();
    private readonly DailyFileLogger _logger;
    private readonly ServiceOptions _options;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly UpdateVisitor _updateVisitor = new();

    private Computer? _computer;
    private string _cachedMetrics = "# No data yet\n";
    private Timer? _refreshTimer;
    private Timer? _writeMetricsTimer;

    public HardwareMetricsProvider(ServiceOptions options, DailyFileLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_computerLock)
        {
            _computer = new Computer
            {
                IsBatteryEnabled = true,
                IsControllerEnabled = true,
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsPowerMonitorEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsNetworkEnabled = true,
                IsPsuEnabled = true,
                IsStorageEnabled = true
            };

            _computer.Open();
        }

        _logger.Info("LibreHardwareMonitor initialized.");

        if (_options.RefreshInterval > TimeSpan.Zero)
        {
            _refreshTimer = new Timer(_ => _ = RefreshInBackgroundAsync(), null, _options.RefreshInterval, _options.RefreshInterval);
        }

        if (_options.WriteMetricsToFile && _options.WriteMetricsInterval > TimeSpan.Zero)
        {
            _writeMetricsTimer = new Timer(_ => _ = WriteMetricsInBackgroundAsync(), null, _options.WriteMetricsInterval, _options.WriteMetricsInterval);
        }

        return RefreshInternalAsync("startup", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _writeMetricsTimer?.Dispose();
        _writeMetricsTimer = null;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            lock (_computerLock)
            {
                if (_computer is not null)
                {
                    _computer.Close();
                    _computer = null;
                }
            }

            _logger.Info("LibreHardwareMonitor shutdown complete.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<string> GetMetricsAsync(CancellationToken cancellationToken)
    {
        // Always refresh for /metrics to expose most recent temperatures.
        await RefreshInternalAsync("/metrics request", cancellationToken);
        return _cachedMetrics;
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshInternalAsync("scheduled refresh", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Scheduled refresh failed", ex);
        }
    }

    private async Task WriteMetricsInBackgroundAsync()
    {
        try
        {
            await RefreshInternalAsync("scheduled file-write refresh", CancellationToken.None);
            WriteInfluxMetricsToFile();
        }
        catch (Exception ex)
        {
            _logger.Error("Scheduled metrics file write failed", ex);
        }
    }

    private async Task RefreshInternalAsync(string reason, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            Computer? computer;
            lock (_computerLock)
            {
                computer = _computer;
            }

            if (computer is null)
                return;

            computer.Accept(_updateVisitor);
            _cachedMetrics = BuildTemperatureMetrics(computer);
            _logger.Debug($"Metrics refreshed ({reason}).");
        }
        catch (Exception ex)
        {
            _logger.Error($"Metrics refresh failed ({reason})", ex);
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void WriteInfluxMetricsToFile()
    {
        Computer? computer;
        lock (_computerLock)
        {
            computer = _computer;
        }

        if (computer is null)
            return;

        string line = BuildInfluxLineProtocol(computer);
        if (string.IsNullOrWhiteSpace(line))
            return;

        string path = Path.Combine(ServiceOptions.InstallDirectory, $"metrics-{DateTime.Now:yyyy-MM-dd}.txt");
        Directory.CreateDirectory(ServiceOptions.InstallDirectory);
        File.AppendAllText(path, line + Environment.NewLine);

        _logger.Debug($"Metrics file write completed: {path}");
    }

    private static string BuildTemperatureMetrics(Computer computer)
    {
        const string metricName = "lhm_temperature_celsius";
        string host = EscapeLabelValue(Environment.MachineName);

        StringBuilder builder = new();
        builder.AppendLine($"# HELP {metricName} Temperature in Celsius from LibreHardwareMonitor sensors.");
        builder.AppendLine($"# TYPE {metricName} gauge");

        foreach (IHardware hardware in computer.Hardware)
        {
            foreach (IHardware current in FlattenHardware(hardware))
            {
                foreach (ISensor sensor in current.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature)
                        continue;

                    if (!sensor.Value.HasValue || float.IsNaN(sensor.Value.Value))
                        continue;

                    string hardwareName = EscapeLabelValue(current.Name);
                    string hardwareType = EscapeLabelValue(current.HardwareType.ToString());
                    string hardwareId = EscapeLabelValue(current.Identifier.ToString());
                    string sensorName = EscapeLabelValue(sensor.Name);
                    string sensorId = EscapeLabelValue(sensor.Identifier.ToString());
                    string value = sensor.Value.Value.ToString(CultureInfo.InvariantCulture);

                    builder.Append(metricName);
                    builder.Append("{host=\"").Append(host).Append("\",");
                    builder.Append("hardware_name=\"").Append(hardwareName).Append("\",");
                    builder.Append("hardware_type=\"").Append(hardwareType).Append("\",");
                    builder.Append("hardware_id=\"").Append(hardwareId).Append("\",");
                    builder.Append("sensor_name=\"").Append(sensorName).Append("\",");
                    builder.Append("sensor_id=\"").Append(sensorId).Append("\"}");
                    builder.Append(' ');
                    builder.AppendLine(value);
                }
            }
        }

        // Prometheus text exposition should be LF-delimited; some parsers reject CRLF.
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string BuildInfluxLineProtocol(Computer computer)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);

        foreach (IHardware hardware in computer.Hardware)
        {
            foreach (IHardware current in FlattenHardware(hardware))
            {
                foreach (ISensor sensor in current.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature)
                        continue;

                    if (!sensor.Value.HasValue || float.IsNaN(sensor.Value.Value))
                        continue;

                    string baseKey = BuildInfluxFieldKey(current, sensor);
                    string key = baseKey;
                    int suffix = 2;

                    while (fields.ContainsKey(key))
                    {
                        key = $"{baseKey}_{suffix++}";
                    }

                    fields[key] = sensor.Value.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        if (fields.Count == 0)
            return string.Empty;

        string hostTag = EscapeInfluxTag(Environment.MachineName);
        string fieldSet = string.Join(',', fields.Select(kv => $"{EscapeInfluxFieldKey(kv.Key)}={kv.Value}"));
        long unixNanoseconds = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100L;

        return $"lhm_sensors,host={hostTag} {fieldSet} {unixNanoseconds}";
    }

    private static string BuildInfluxFieldKey(IHardware hardware, ISensor sensor)
    {
        string raw = $"{hardware.HardwareType}_{hardware.Name}_{sensor.Name}";
        string sanitized = Regex.Replace(raw, "[^A-Za-z0-9_]+", "_");
        sanitized = sanitized.Trim('_');

        if (string.IsNullOrWhiteSpace(sanitized))
            return "temp";

        return sanitized.ToLowerInvariant();
    }

    private static string EscapeInfluxTag(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal);
    }

    private static string EscapeInfluxFieldKey(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal);
    }

    private static IEnumerable<IHardware> FlattenHardware(IHardware hardware)
    {
        yield return hardware;

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            foreach (IHardware nested in FlattenHardware(subHardware))
                yield return nested;
        }
    }

    private static string EscapeLabelValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _writeMetricsTimer?.Dispose();
        _refreshLock.Dispose();
    }
}