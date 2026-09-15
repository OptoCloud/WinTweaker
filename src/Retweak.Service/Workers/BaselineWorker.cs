using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Retweak.Service.Configuration;
using Retweak.Service.Core;
using Retweak.Service.Infra;

namespace Retweak.Service.Workers;

/// <summary>
/// Drives enforcement. Three independent loops run concurrently:
///   1. the trigger loop, which reacts to registry change notifications;
///   2. the session loop, which reacts to logon/logoff;
///   3. the timer loop, which reconciles hives and runs the periodic audit.
///
/// All three funnel into <see cref="BaselineManager"/>, which serialises them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BaselineWorker : BackgroundService
{
    private readonly RetweakOptions _options;
    private readonly BaselineManager _baseline;
    private readonly WatcherManager _watchers;
    private readonly ServiceBaselineManager _services;
    private readonly ScheduledTaskBaselineManager _scheduledTasks;
    private readonly ChangeTrigger _trigger;
    private readonly SessionEventBus _sessions;
    private readonly ILogger<BaselineWorker> _log;

    /// <summary>Ceiling on the exponential backoff applied when a loop faults repeatedly.</summary>
    private const int MaxLoopBackoffSeconds = 60;

    /// <summary>
    /// Caps the exponent in <c>1 &lt;&lt; consecutiveFailures</c> so the shift itself never
    /// overflows; the <see cref="MaxLoopBackoffSeconds"/> clamp makes anything past this moot.
    /// </summary>
    private const int MaxLoopBackoffShift = 6;

    /// <summary>Ceiling on the logon hive-poll backoff, so a slow profile load is still polled often enough.</summary>
    private static readonly TimeSpan MaxLogonPollDelay = TimeSpan.FromSeconds(5);

    /// <summary>Growth factor applied to the logon hive-poll delay after each unsuccessful attempt.</summary>
    private const double LogonPollBackoffFactor = 1.5;

    public BaselineWorker(
        IOptions<RetweakOptions> options,
        BaselineManager baseline,
        WatcherManager watchers,
        ServiceBaselineManager services,
        ScheduledTaskBaselineManager scheduledTasks,
        ChangeTrigger trigger,
        SessionEventBus sessions,
        ILogger<BaselineWorker> log)
    {
        _options = options.Value;
        _baseline = baseline;
        _watchers = watchers;
        _services = services;
        _scheduledTasks = scheduledTasks;
        _trigger = trigger;
        _sessions = sessions;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Retweak starting. {Entries} baseline entries, view={View}, audit every {Audit} min.",
            _options.Entries.Count,
            RegistryHelpers.NativeView,
            _options.AuditIntervalMinutes);

        // Apply before arming watchers. Doing it the other way round would race: a change
        // between arming and the first pass would be seen, but a change before arming and
        // after the pass would not.
        PassReport initial = _baseline.ApplyBaseline("startup");
        _log.LogInformation(
            "Initial pass: {Checked} checked, {Repaired} repaired, {Failed} failed.",
            initial.Checked, initial.Repaired, initial.Failed);

        ApplyServicesAndScheduledTasks("startup");

        _watchers.EnsureMachineWatchers();
        foreach (string sid in _watchers.ReconcileUserWatchers())
        {
            _baseline.ApplyForUser(sid, "startup-hive");
        }

        // A second reconcile catches keys that the initial pass had to create: a watcher
        // cannot be armed on a key that did not exist a moment ago.
        _watchers.EnsureMachineWatchers();
        _watchers.ReconcileUserWatchers();

        _log.LogInformation("Enforcement active with {Count} watcher(s).", _watchers.WatcherCount);

        Task[] loops =
        [
            RunGuarded(() => TriggerLoopAsync(stoppingToken), "trigger", stoppingToken),
            RunGuarded(() => SessionLoopAsync(stoppingToken), "session", stoppingToken),
            RunGuarded(() => TimerLoopAsync(stoppingToken), "timer", stoppingToken),
        ];

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>
    /// Restarts a loop if it ever throws. A BackgroundService whose ExecuteAsync faults
    /// takes the whole host with it under the default HostOptions, which for an
    /// enforcement service means silent non-enforcement until someone notices.
    /// </summary>
    private async Task RunGuarded(Func<Task> body, string name, CancellationToken stoppingToken)
    {
        int consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await body().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                TimeSpan delay = TimeSpan.FromSeconds(
                    Math.Min(MaxLoopBackoffSeconds, 1 << Math.Min(MaxLoopBackoffShift, consecutiveFailures)));
                _log.LogError(ex, "Loop '{Loop}' faulted (attempt {Attempt}); restarting in {Delay}.",
                    name, consecutiveFailures, delay);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task TriggerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string source = await _trigger.WaitAsync(stoppingToken).ConfigureAwait(false);

            // Let a burst settle. Explorer in particular writes several values in quick
            // succession when a folder option changes.
            if (_options.Debounce > TimeSpan.Zero)
            {
                await Task.Delay(_options.Debounce, stoppingToken).ConfigureAwait(false);
            }

            var sw = Stopwatch.StartNew();
            PassReport report = _baseline.ApplyBaseline($"watch:{source}");

            if (report.Repaired > 0)
            {
                _log.LogDebug("Repaired {Count} value(s) {Ms} ms after notification on {Source}.",
                    report.Repaired, (int)sw.Elapsed.TotalMilliseconds, source);
            }

            // A change may have created a key we could not previously watch.
            _watchers.EnsureMachineWatchers();
        }
    }

    private async Task SessionLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (SessionEvent evt in _sessions.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (evt.Kind == SessionEventKind.Logoff)
            {
                // Do not tear watchers down on the logoff notification itself: the hive is
                // usually still loaded and may stay loaded for a while. Reconciliation
                // removes them once the hive is genuinely gone.
                _log.LogDebug("Logoff on session {Session}; deferring watcher cleanup to reconcile.", evt.SessionId);
                _watchers.ReconcileUserWatchers();
                continue;
            }

            await HandleLogonAsync(evt, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the per-user baseline as soon as the user's hive becomes available. The
    /// logon notification can precede the profile load, so this retries for a bounded time.
    /// </summary>
    private async Task HandleLogonAsync(SessionEvent evt, CancellationToken stoppingToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.LogonHiveWaitSeconds));
        var delay = TimeSpan.FromSeconds(1);

        while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            if (evt.Sid is { Length: > 0 } sid)
            {
                if (UserHives.IsUserSid(sid) && UserHives.IsHiveUsable(RegistryHelpers.NativeView, sid))
                {
                    PassReport report = _baseline.ApplyForUser(sid, "logon");
                    _watchers.ReconcileUserWatchers();
                    _log.LogInformation(
                        "Logon session {Session} ({Sid}): {Checked} checked, {Repaired} repaired.",
                        evt.SessionId, sid, report.Checked, report.Repaired);
                    return;
                }
            }
            else
            {
                // No SID resolved: fall back to diffing HKU. Any hive that showed up is
                // almost certainly the one that just logged on.
                IReadOnlyList<string> added = _watchers.ReconcileUserWatchers();
                if (added.Count > 0)
                {
                    foreach (string newSid in added)
                    {
                        _baseline.ApplyForUser(newSid, "logon-reconcile");
                    }

                    _watchers.ReconcileUserWatchers();
                    _log.LogInformation(
                        "Logon session {Session}: applied baseline to {Count} newly loaded hive(s).",
                        evt.SessionId, added.Count);
                    return;
                }
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(MaxLogonPollDelay.TotalSeconds, delay.TotalSeconds * LogonPollBackoffFactor));
        }

        _log.LogWarning(
            "Session {Session} logon: hive did not become available within {Seconds}s; the periodic audit will cover it.",
            evt.SessionId, _options.LogonHiveWaitSeconds);
    }

    private async Task TimerLoopAsync(CancellationToken stoppingToken)
    {
        using var reconcileTimer = new PeriodicTimer(_options.HiveReconcileInterval);
        DateTime nextAudit = DateTime.UtcNow + _options.AuditInterval;

        while (await reconcileTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (string sid in _watchers.ReconcileUserWatchers())
            {
                _baseline.ApplyForUser(sid, "reconcile");
            }

            _watchers.EnsureMachineWatchers();

            if (DateTime.UtcNow < nextAudit)
            {
                continue;
            }

            nextAudit = DateTime.UtcNow + _options.AuditInterval;
            PassReport report = _baseline.ApplyBaseline("audit");

            _log.LogInformation(
                "Audit: {Checked} checked, {Repaired} repaired, {Failed} failed, {Watchers} watcher(s), {Sids} hive(s) tracked, {Ms} ms.",
                report.Checked, report.Repaired, report.Failed,
                _watchers.WatcherCount, _watchers.WatchedSids.Count,
                (int)report.Elapsed.TotalMilliseconds);

            ApplyServicesAndScheduledTasks("audit");
        }
    }

    /// <summary>
    /// Applies the services and scheduled-task baselines. Unlike the registry baseline
    /// these have no cheap change notification, so this only runs here: once at startup
    /// and once per audit interval, never reactively.
    /// </summary>
    private void ApplyServicesAndScheduledTasks(string source)
    {
        if (_options.ServiceEntries.Count > 0)
        {
            PassReport services = _services.ApplyBaseline(source);
            _log.LogInformation(
                "Services ({Source}): {Checked} checked, {Ok} ok, {Failed} failed.",
                source, services.Checked, services.Repaired, services.Failed);
        }

        if (_options.ScheduledTaskEntries.Count > 0)
        {
            PassReport tasks = _scheduledTasks.ApplyBaseline(source);
            _log.LogInformation(
                "Scheduled tasks ({Source}): {Checked} checked, {Ok} ok, {Failed} failed.",
                source, tasks.Checked, tasks.Repaired, tasks.Failed);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Stopping; releasing watchers.");
        _watchers.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
