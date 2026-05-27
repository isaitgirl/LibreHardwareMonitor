namespace LibreHardwareMonitorService;

internal sealed class DailyFileLogger
{
    private readonly object _lock = new();
    private readonly ServiceLogLevel _minimumLevel;
    private readonly string _rootDirectory;

    public DailyFileLogger(string rootDirectory, ServiceLogLevel minimumLevel)
    {
        _rootDirectory = rootDirectory;
        _minimumLevel = minimumLevel;
    }

    public void Info(string message) => Write(ServiceLogLevel.Info, message);

    public void Debug(string message) => Write(ServiceLogLevel.Debug, message);

    public void Error(string message, Exception? exception = null)
    {
        string fullMessage = exception is null ? message : $"{message}. Exception: {exception}";
        Write(ServiceLogLevel.Info, $"ERROR {fullMessage}");
    }

    private void Write(ServiceLogLevel level, string message)
    {
        if (level < _minimumLevel)
            return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"[{timestamp}] [{level.ToString().ToUpperInvariant()}] {message}";
        string logPath = Path.Combine(_rootDirectory, $"LibreHardwareMonitorService-{DateTime.Now:yyyy-MM-dd}.log");

        lock (_lock)
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }

        Console.WriteLine(line);
    }
}