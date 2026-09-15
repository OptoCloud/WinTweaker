using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Retweak.Service.Configuration;

namespace Retweak.Service.Core;

/// <summary>
/// Enforces <see cref="ServiceEntry"/> baseline: start type via the registry (the same
/// value SCM itself reads and `sc.exe config` writes), running state via the Service
/// Control Manager. Unlike <see cref="BaselineManager"/> there is no cheap change
/// notification for a service's running state, so this only runs at startup and on the
/// audit cadence, not reactively.
/// </summary>
public sealed class ServiceBaselineManager
{
    /// <summary>How long to wait for a Start/Stop request to actually take effect.</summary>
    private static readonly TimeSpan ServiceControlTimeout = TimeSpan.FromSeconds(30);

    private readonly RetweakOptions _options;
    private readonly ILogger<ServiceBaselineManager> _log;
    private readonly RegistryView _view = RegistryHelpers.NativeView;

    public ServiceBaselineManager(IOptions<RetweakOptions> options, ILogger<ServiceBaselineManager> log)
    {
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Pure compliance check for a service's Start registry value, split out from
    /// <see cref="ApplyStartType"/> so it is testable without a live registry.
    /// </summary>
    internal static bool IsStartTypeCompliant(object? actual, ServiceStartMode desired) =>
        actual is int current && current == (int)desired;

    /// <summary>
    /// Pure compliance check for the "Automatic (Delayed Start)" flag, which lives in a
    /// separate DWORD alongside Start and is otherwise invisible to <see cref="IsStartTypeCompliant"/>:
    /// a service left in delayed-auto state reads as plain Automatic and would never be
    /// corrected without this. <see cref="ServiceStartMode.Automatic"/> here always means the
    /// non-delayed form, since the config schema has no way to request delayed start.
    /// </summary>
    internal static bool IsDelayedAutostartCompliant(object? actual, ServiceStartMode desired) =>
        desired != ServiceStartMode.Automatic || actual is not int flag || flag == 0;

    /// <summary>
    /// Pure compliance check for a service's running state, split out from
    /// <see cref="ApplyRunState"/> so it is testable without a live SCM.
    /// </summary>
    internal static bool IsRunStateCompliant(ServiceControllerStatus actual, DesiredServiceRunState desired) =>
        desired switch
        {
            DesiredServiceRunState.Running => actual == ServiceControllerStatus.Running,
            DesiredServiceRunState.Stopped => actual == ServiceControllerStatus.Stopped,
            _ => true,
        };

    public PassReport ApplyBaseline(string source)
    {
        var sw = Stopwatch.StartNew();
        int examined = 0, repaired = 0, failed = 0;

        foreach (ServiceEntry entry in _options.ServiceEntries)
        {
            examined++;

            bool ok = true;
            if (entry.ResolvedStartType is { } startType)
            {
                ok &= ApplyStartType(entry, startType, source);
            }

            if (entry.ResolvedRunState is { } runState)
            {
                ok &= ApplyRunState(entry, runState);
            }

            // "Repaired" here just means "no failure"; unlike the registry baseline this
            // manager does not distinguish "already compliant" from "successfully fixed"
            // at the PassReport level, since callers only care whether the pass is healthy.
            if (ok)
            {
                repaired++;
            }
            else
            {
                failed++;
            }
        }

        return new PassReport(examined, repaired, failed, sw.Elapsed);
    }

    private bool ApplyStartType(ServiceEntry entry, ServiceStartMode desired, string source)
    {
        string id = entry.DescribeAt();
        string path = $@"SYSTEM\CurrentControlSet\Services\{entry.Name}";

        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, _view);
            using RegistryKey? key = hklm.OpenSubKey(path, writable: true);
            if (key is null)
            {
                _log.LogError("Service {Id}: no such service (registry key not found).", id);
                return false;
            }

            object? actual = key.GetValue("Start");
            object? delayedActual = key.GetValue("DelayedAutostart");
            bool startCompliant = IsStartTypeCompliant(actual, desired);
            bool delayCompliant = IsDelayedAutostartCompliant(delayedActual, desired);

            if (startCompliant && delayCompliant)
            {
                return true;
            }

            if (!startCompliant)
            {
                _log.LogWarning(
                    "Drift: {Id} start type was {Actual}, expected {Desired}; trigger={Source}.",
                    id, actual, desired, source);

                key.SetValue("Start", (int)desired, RegistryValueKind.DWord);
            }

            if (!delayCompliant)
            {
                _log.LogWarning(
                    "Drift: {Id} was Automatic (Delayed Start), expected plain Automatic; trigger={Source}.",
                    id, source);

                key.SetValue("DelayedAutostart", 0, RegistryValueKind.DWord);
            }

            _log.LogInformation("Enforced: {Id} start type set to {Desired}.", id, desired);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed enforcing start type for {Id}.", id);
            return false;
        }
    }

    private bool ApplyRunState(ServiceEntry entry, DesiredServiceRunState desired)
    {
        string id = entry.DescribeAt();

        try
        {
            using var sc = new ServiceController(entry.Name);
            sc.Refresh();

            if (IsRunStateCompliant(sc.Status, desired))
            {
                return true;
            }

            _log.LogWarning("Drift: {Id} was {Actual}, expected {Desired}.", id, sc.Status, desired);

            switch (desired)
            {
                case DesiredServiceRunState.Running:
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, ServiceControlTimeout);
                    break;

                case DesiredServiceRunState.Stopped:
                    if (!sc.CanStop)
                    {
                        _log.LogError("Service {Id} cannot be stopped (CanStop is false).", id);
                        return false;
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceControlTimeout);
                    break;
            }

            _log.LogInformation("Enforced: {Id} run state set to {Desired}.", id, desired);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed enforcing run state for {Id}.", id);
            return false;
        }
    }
}
