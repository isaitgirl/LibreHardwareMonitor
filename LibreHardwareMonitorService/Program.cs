using LibreHardwareMonitorService;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Security.Principal;

string installDirectory = OperatingSystem.IsWindows()
    ? ServiceOptions.InstallDirectory
    : AppContext.BaseDirectory;

Directory.CreateDirectory(installDirectory);

string configPath = Path.Combine(installDirectory, "serviceconfig.json");
if (!File.Exists(configPath))
{
    File.WriteAllText(configPath, ServiceOptions.DefaultConfigJson);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = installDirectory
});

bool isWindowsService = WindowsServiceHelpers.IsWindowsService();
if (isWindowsService)
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "LibreHardwareMonitorService";
    });
}

builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "LHM_");

ServiceOptions serviceOptions = ServiceOptions.FromConfiguration(builder.Configuration);
DailyFileLogger logger = new(installDirectory, serviceOptions.LogLevel);

bool isElevatedConsole = true;
if (OperatingSystem.IsWindows() && !isWindowsService)
{
    isElevatedConsole = IsCurrentProcessElevated();
}

builder.Services.AddSingleton(serviceOptions);
builder.Services.AddSingleton(logger);
builder.Services.AddSingleton<HardwareMetricsProvider>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareMetricsProvider>());

builder.WebHost.UseUrls($"http://0.0.0.0:{serviceOptions.HttpPort}");

WebApplication app = builder.Build();

app.Lifetime.ApplicationStarted.Register(() =>
{
    logger.Info($"Service started in {(isWindowsService ? "windows-service" : "console")} mode. Metrics endpoint on http://0.0.0.0:{serviceOptions.HttpPort}/metrics");
    if (!isWindowsService && OperatingSystem.IsWindows() && !isElevatedConsole)
    {
        logger.Info("WARNING: running in non-elevated console mode. Sensor access can be incomplete; run as Administrator for service-equivalent hardware metrics.");
    }
});
app.Lifetime.ApplicationStopping.Register(() => logger.Info("Service stopping."));

app.MapGet("/health", () =>
{
    logger.Info("GET /health");
    return Results.Ok("OK");
});

app.MapGet("/metrics", async (HardwareMetricsProvider provider, CancellationToken cancellationToken) =>
{
    logger.Info("GET /metrics");
    string metrics = await provider.GetMetricsAsync(cancellationToken);
    return Results.Text(metrics, "text/plain; version=0.0.4; charset=utf-8");
});

await app.RunAsync();

static bool IsCurrentProcessElevated()
{
    try
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch
    {
        return false;
    }
}