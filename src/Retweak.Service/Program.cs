using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Retweak.Service.Configuration;
using Retweak.Service.Core;
using Retweak.Service.Infra;
using Retweak.Service.Workers;

const string RegistryConfigPath = @"SOFTWARE\OptoCloud\Retweak\Config";

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// A single-file published service has its content root at the extraction directory unless
// told otherwise, and SCM starts services in system32. Pin both to the executable.
string exeDir = AppContext.BaseDirectory;
builder.Environment.ContentRootPath = exeDir;

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(exeDir)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddRegistryOverrides(RegistryConfigPath, RetweakOptions.SectionName, RegistryHelpers.NativeView)
    .AddEnvironmentVariables("RETWEAK_")
    .AddCommandLine(args);

builder.Services
    .AddOptions<RetweakOptions>()
    .Bind(builder.Configuration.GetSection(RetweakOptions.SectionName))
    .Validate(ValidateOptions, "Baseline configuration is invalid; see the log for details.")
    .ValidateOnStart();

builder.Services.Configure<ServiceControlOptions>(
    builder.Configuration.GetSection(ServiceControlOptions.SectionName));

// ---- logging ---------------------------------------------------------------
// The file log is always present. It is the only thing that can report a failure that
// happens before, or because of, the Event Log source being missing.
RetweakOptions bootOptions =
    builder.Configuration.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>() ?? new RetweakOptions();

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(
    bootOptions.LogDirectory,
    "retweak",
    bootOptions.LogFileMaxBytes,
    bootOptions.LogFileRetainCount));

if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
}

// Adding the Event Log provider with a source that does not exist throws on first write,
// which would take down the very logging we need. Probe first, and tolerate the probe
// itself failing (it needs read access to the registry's event log configuration).
if (EventLogSourceUsable(bootOptions.EventSourceName, bootOptions.EventLogName))
{
    builder.Logging.AddEventLog(new EventLogSettings
    {
        SourceName = bootOptions.EventSourceName,
        LogName = bootOptions.EventLogName,
    });
}

// ---- services --------------------------------------------------------------
builder.Services.AddSingleton<ChangeTrigger>();
builder.Services.AddSingleton<SessionEventBus>();
builder.Services.AddSingleton<BaselineManager>();
builder.Services.AddSingleton<WatcherManager>();
builder.Services.AddHostedService<BaselineWorker>();

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(o => o.ServiceName = "RetweakService");

    // Must come after AddWindowsService so it replaces the default WindowsServiceLifetime.
    builder.Services.Replace(
        ServiceDescriptor.Singleton<IHostLifetime, SessionAwareLifetime>());
}

// Never let a transient fault in one hosted service tear the process down; SCM restart is
// the backstop, not the first line of defence.
builder.Services.Configure<HostOptions>(o =>
{
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    o.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

IHost host = builder.Build();
await host.RunAsync();

static bool ValidateOptions(RetweakOptions options)
{
    if (options.Entries.Count == 0)
    {
        return false;
    }

    foreach (BaselineEntry entry in options.Entries)
    {
        if (!entry.TryValidate(out string error))
        {
            Console.Error.WriteLine($"Invalid baseline entry {entry.Path}!{entry.Name}: {error}");
            return false;
        }
    }

    return true;
}

static bool EventLogSourceUsable(string source, string logName)
{
    try
    {
        return EventLog.SourceExists(source) &&
               string.Equals(EventLog.LogNameFromSourceName(source, "."), logName, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}
