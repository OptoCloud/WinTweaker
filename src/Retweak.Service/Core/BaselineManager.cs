using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Retweak.Service.Configuration;

namespace Retweak.Service.Core;

public enum EntryOutcome
{
    Compliant,
    Repaired,
    Failed,
}

public readonly record struct PassReport(int Checked, int Repaired, int Failed, TimeSpan Elapsed)
{
    public bool Clean => Repaired == 0 && Failed == 0;
}

/// <summary>
/// Owns all read/modify/write of the enforced values.
///
/// Serialisation model: only one pass runs at a time. A trigger arriving while a pass is
/// in flight does not queue up a second thread, it sets a rerun flag so exactly one extra
/// pass follows. That bounds the work under a storm of notifications while guaranteeing
/// that the final state of the registry is always observed by a pass that started after it.
/// </summary>
public sealed class BaselineManager
{
    private readonly RetweakOptions _options;
    private readonly ILogger<BaselineManager> _log;
    private readonly NormOptions _norm;
    private readonly RegistryView _view = RegistryHelpers.NativeView;

    // Keys currently known to be non-compliant. Used so that persistent drift is logged
    // once rather than on every pass, and so the return to compliance is logged exactly once.
    private readonly HashSet<string> _noncompliant = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _applyGate = new();
    private bool _applyInProgress;
    private bool _rerunRequested;

    private long _lastSelfWriteTicks = long.MinValue;

    public BaselineManager(IOptions<RetweakOptions> options, ILogger<BaselineManager> log)
    {
        _options = options.Value;
        _log = log;
        _norm = new NormOptions(
            _options.CaseInsensitiveStringCompare,
            _options.AllowExpandStringEquivalence);
    }

    /// <summary>
    /// True when the last write we performed was recent enough that an incoming change
    /// notification is most likely our own echo. Used to suppress noisy logging only; the
    /// pass still runs, because suppressing the pass itself would drop genuine changes
    /// that happen to land inside the window.
    /// </summary>
    public bool IsLikelySelfEcho() =>
        Environment.TickCount64 - Interlocked.Read(ref _lastSelfWriteTicks) < _options.DebounceMilliseconds;

    /// <summary>
    /// Applies the full baseline: every machine entry, plus every per-user entry against
    /// every currently loaded user hive. Idempotent; values are written only when they
    /// differ, which is what stops watcher feedback loops.
    /// </summary>
    public PassReport ApplyBaseline(string source)
    {
        lock (_applyGate)
        {
            if (_applyInProgress)
            {
                _rerunRequested = true;
                _log.LogTrace("Pass already running; rerun requested by {Source}.", source);
                return default;
            }

            _applyInProgress = true;
        }

        try
        {
            PassReport last;
            while (true)
            {
                last = RunPass(source);

                lock (_applyGate)
                {
                    if (!_rerunRequested)
                    {
                        return last;
                    }

                    _rerunRequested = false;
                }
            }
        }
        finally
        {
            lock (_applyGate)
            {
                _applyInProgress = false;
            }
        }
    }

    /// <summary>
    /// Read-only compliance sweep. Does not repair and does not touch the drift set, so
    /// it can be called for reporting without disturbing the logging state machine.
    /// </summary>
    public PassReport IsCompliant()
    {
        var sw = Stopwatch.StartNew();
        int examined = 0, drifted = 0, failed = 0;

        ForEachTarget((root, path, entry, scopeTag) =>
        {
            examined++;
            try
            {
                if (!ReadAndCompare(root, path, entry, out _, out _))
                {
                    drifted++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex, "Compliance check failed for {Id}.", entry.DescribeAt(scopeTag));
            }
        });

        return new PassReport(examined, drifted, failed, sw.Elapsed);
    }

    /// <summary>
    /// Applies only the per-user entries for one SID. Called on logon so a new user is
    /// covered without waiting for the next full pass.
    /// </summary>
    public PassReport ApplyForUser(string sid, string source)
    {
        var sw = Stopwatch.StartNew();
        int examined = 0, repaired = 0, failed = 0;

        try
        {
            using RegistryKey hku = RegistryKey.OpenBaseKey(RegistryHive.Users, _view);
            foreach (BaselineEntry entry in _options.PerUserEntries)
            {
                examined++;
                EntryOutcome outcome = ApplyOne(hku, $"{sid}\\{entry.Path}", entry, $"HKU\\{sid}", source);
                if (outcome == EntryOutcome.Repaired)
                {
                    repaired++;
                }
                else if (outcome == EntryOutcome.Failed)
                {
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            failed++;
            _log.LogError(ex, "Per-user pass failed for {Sid}.", sid);
        }

        return new PassReport(examined, repaired, failed, sw.Elapsed);
    }

    /// <summary>
    /// Drops drift bookkeeping for a SID whose hive has gone away, so that the same user
    /// logging back in gets a fresh "drift detected" line rather than silence.
    /// </summary>
    public void ForgetUser(string sid)
    {
        lock (_applyGate)
        {
            _noncompliant.RemoveWhere(id => id.StartsWith($"HKU\\{sid}\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    private PassReport RunPass(string source)
    {
        var sw = Stopwatch.StartNew();
        int examined = 0, repaired = 0, failed = 0;

        ForEachTarget((root, path, entry, scopeTag) =>
        {
            examined++;
            var outcome = ApplyOne(root, path, entry, scopeTag, source);
            switch (outcome)
            {
                case EntryOutcome.Repaired:
                    repaired++;
                    break;
                case EntryOutcome.Failed:
                    failed++;
                    break;
                case EntryOutcome.Compliant:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        });

        var report = new PassReport(examined, repaired, failed, sw.Elapsed);

        if (report.Clean)
        {
            _log.LogDebug(
                "Pass ({Source}): {Checked} values compliant in {Ms} ms.",
                source, examined, (int)sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            _log.LogInformation(
                "Pass ({Source}): {Checked} checked, {Repaired} repaired, {Failed} failed in {Ms} ms.",
                source, examined, repaired, failed, (int)sw.Elapsed.TotalMilliseconds);
        }

        return report;
    }

    /// <summary>
    /// Walks every (hive root, resolved path, entry) triple the baseline covers, opening
    /// each hive once. Per-user work is wrapped so that one unreadable hive cannot abort
    /// the pass for the other users.
    /// </summary>
    private void ForEachTarget(Action<RegistryKey, string, BaselineEntry, string> visit)
    {
        List<BaselineEntry> machine = [.. _options.MachineEntries];
        if (machine.Count > 0)
        {
            try
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, _view);
                foreach (BaselineEntry entry in machine)
                {
                    visit(hklm, entry.Path, entry, "HKLM");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Machine scope pass failed.");
            }
        }

        List<BaselineEntry> perUser = [.. _options.PerUserEntries];
        if (perUser.Count == 0)
        {
            return;
        }

        try
        {
            using RegistryKey hku = RegistryKey.OpenBaseKey(RegistryHive.Users, _view);
            foreach (string sid in UserHives.GetLoadedUserSids(hku))
            {
                foreach (BaselineEntry entry in perUser)
                {
                    try
                    {
                        visit(hku, $"{sid}\\{entry.Path}", entry, $"HKU\\{sid}");
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Entry failed for {Sid} at {Path}.", sid, entry.Path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "User scope pass failed.");
        }
    }

    private EntryOutcome ApplyOne(
        RegistryKey root,
        string path,
        BaselineEntry entry,
        string scopeTag,
        string source)
    {
        string id = entry.DescribeAt(scopeTag);

        try
        {
            bool compliant = ReadAndCompare(root, path, entry, out object? actual, out RegistryValueKind actualKind);

            if (compliant)
            {
                bool wasDrifted;
                lock (_applyGate)
                {
                    wasDrifted = _noncompliant.Remove(id);
                }

                if (wasDrifted)
                {
                    _log.LogInformation("Restored: {Id} is compliant again.", id);
                }

                return EntryOutcome.Compliant;
            }

            bool firstReport;
            lock (_applyGate)
            {
                firstReport = _noncompliant.Add(id);
            }

            if (firstReport && !IsLikelySelfEcho())
            {
                _log.LogWarning(
                    "Drift: {Id} was {Actual} ({ActualKind}), expected {Desired} ({DesiredKind}); trigger={Source}.",
                    id,
                    RegistryHelpers.Describe(actual),
                    actualKind,
                    entry.Kind == RegistryValueKind.MultiString ? string.Join('|', entry.Values) : entry.Value,
                    entry.Kind,
                    source);
            }

            using var writable = root.CreateSubKey(path, writable: true);
            if (writable is null)
            {
                _log.LogError("Could not open or create {Id} for writing.", id);
                return EntryOutcome.Failed;
            }

            writable.SetValue(entry.Name, entry.ParseDesiredValue(), entry.Kind);
            Interlocked.Exchange(ref _lastSelfWriteTicks, Environment.TickCount64);

            _log.LogInformation("Enforced: {Id} set to {Desired}.",
                id,
                entry.Kind == RegistryValueKind.MultiString ? string.Join('|', entry.Values) : entry.Value);

            return EntryOutcome.Repaired;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogError(ex, "Access denied enforcing {Id}. Check the service account has write access to this key.", id);
            return EntryOutcome.Failed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed enforcing {Id}.", id);
            return EntryOutcome.Failed;
        }
    }

    private bool ReadAndCompare(
        RegistryKey root,
        string path,
        BaselineEntry entry,
        out object? actual,
        out RegistryValueKind actualKind)
    {
        actual = null;
        actualKind = RegistryValueKind.Unknown;

        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        if (key is null)
        {
            return false;
        }

        // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ in its stored form so that
        // EqualsNorm decides whether expansion is appropriate, rather than the read.
        actual = key.GetValue(entry.Name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (actual is null)
        {
            return false;
        }

        actualKind = key.GetValueKind(entry.Name);

        return RegistryHelpers.EqualsNorm(entry.Kind, entry.ParseDesiredValue(), actualKind, actual, _norm);
    }
}
