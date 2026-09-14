using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Retweak.Service.Configuration;
using Retweak.Service.Core;

namespace Retweak.Service.Infra;

/// <summary>
/// Owns every <see cref="RegistryWatcher"/> and keeps the set of user-hive watchers in
/// step with the set of loaded hives.
///
/// Reconciliation, not logon events, is the source of truth. Session notifications are
/// only a hint that reconciling now is worthwhile: hives load asynchronously after logon,
/// unload some time after logoff, and RunAs/scheduled-task logons load hives without ever
/// producing a console session change. Diffing HKU against the watcher map handles all of
/// those uniformly.
/// </summary>
public sealed class WatcherManager : IDisposable
{
    private readonly RetweakOptions _options;
    private readonly ILogger<WatcherManager> _log;
    private readonly ILogger _watcherLog;
    private readonly ChangeTrigger _trigger;

    private readonly object _gate = new();
    private readonly List<RegistryWatcher> _machineWatchers = [];
    private readonly Dictionary<string, List<RegistryWatcher>> _userWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public WatcherManager(
        IOptions<RetweakOptions> options,
        ILoggerFactory loggerFactory,
        ChangeTrigger trigger)
    {
        _options = options.Value;
        _log = loggerFactory.CreateLogger<WatcherManager>();
        _watcherLog = loggerFactory.CreateLogger<RegistryWatcher>();
        _trigger = trigger;
    }

    public int WatcherCount
    {
        get
        {
            lock (_gate)
            {
                return _machineWatchers.Count + _userWatchers.Sum(kv => kv.Value.Count);
            }
        }
    }

    public IReadOnlyCollection<string> WatchedSids
    {
        get
        {
            lock (_gate)
            {
                return [.. _userWatchers.Keys];
            }
        }
    }

    /// <summary>
    /// (Re)creates machine watchers. Safe to call repeatedly: keys that did not exist on a
    /// previous attempt get picked up once something creates them.
    /// </summary>
    public void EnsureMachineWatchers()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = _options.MachineEntries
                .Where(e => e.Watch)
                .Select(e => e.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in wanted)
            {
                string description = $"HKLM\\{path}";
                if (_machineWatchers.Any(w => string.Equals(w.Description, description, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                RegistryWatcher? watcher = RegistryWatcher.TryCreate(
                    NativeMethods.HKEY_LOCAL_MACHINE,
                    path,
                    RegistryHelpers.UseWow64_64Key,
                    description,
                    w => _trigger.Request(w.Description),
                    _watcherLog);

                if (watcher is not null)
                {
                    _machineWatchers.Add(watcher);
                }
            }
        }
    }

    /// <summary>
    /// Adds watchers for hives that appeared and removes watchers for hives that went away.
    /// Returns the SIDs newly added, so the caller can apply the baseline to them at once.
    /// </summary>
    public IReadOnlyList<string> ReconcileUserWatchers()
    {
        IReadOnlyList<string> loaded;
        try
        {
            loaded = UserHives.GetLoadedUserSids(RegistryHelpers.NativeView);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not enumerate HKEY_USERS.");
            return [];
        }

        var added = new List<string>();
        List<RegistryWatcher> toDispose = [];

        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            var loadedSet = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);

            foreach (string sid in _userWatchers.Keys.Where(s => !loadedSet.Contains(s)).ToList())
            {
                toDispose.AddRange(_userWatchers[sid]);
                _userWatchers.Remove(sid);
                _log.LogInformation("Hive for {Sid} unloaded; watchers removed.", sid);
            }

            foreach (string sid in loaded)
            {
                if (_userWatchers.ContainsKey(sid))
                {
                    continue;
                }

                List<RegistryWatcher> created = CreateUserWatchers(sid);
                if (created.Count > 0)
                {
                    _userWatchers[sid] = created;
                    added.Add(sid);
                    _log.LogInformation("Hive for {Sid} loaded; {Count} watcher(s) installed.", sid, created.Count);
                }
                else if (_options.PerUserEntries.Any(e => e.Watch))
                {
                    // The key does not exist yet. Applying the baseline will create it, and
                    // the next reconcile picks the watcher up. Report it as added so the
                    // caller applies now rather than waiting for the audit.
                    added.Add(sid);
                }
            }
        }

        foreach (RegistryWatcher w in toDispose)
        {
            w.Dispose();
        }

        return added;
    }

    /// <summary>
    /// Explicitly drops a SID's watchers, used on logoff so handles are released promptly
    /// rather than at the next reconcile tick.
    /// </summary>
    public void RemoveUser(string sid)
    {
        List<RegistryWatcher>? watchers;
        lock (_gate)
        {
            if (!_userWatchers.Remove(sid, out watchers))
            {
                return;
            }
        }

        foreach (RegistryWatcher w in watchers)
        {
            w.Dispose();
        }

        _log.LogInformation("Removed {Count} watcher(s) for {Sid}.", watchers.Count, sid);
    }

    private List<RegistryWatcher> CreateUserWatchers(string sid)
    {
        var created = new List<RegistryWatcher>();

        foreach (string path in _options.PerUserEntries
                     .Where(e => e.Watch)
                     .Select(e => e.Path)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string subKey = $"{sid}\\{path}";
            RegistryWatcher? watcher = RegistryWatcher.TryCreate(
                NativeMethods.HKEY_USERS,
                subKey,
                RegistryHelpers.UseWow64_64Key,
                $"HKU\\{subKey}",
                w => _trigger.Request(w.Description),
                _watcherLog,
                tag: sid);

            if (watcher is not null)
            {
                created.Add(watcher);
            }
        }

        return created;
    }

    public void Dispose()
    {
        List<RegistryWatcher> all;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _machineWatchers, .. _userWatchers.SelectMany(kv => kv.Value)];
            _machineWatchers.Clear();
            _userWatchers.Clear();
        }

        foreach (RegistryWatcher w in all)
        {
            w.Dispose();
        }

        _log.LogInformation("Disposed {Count} watcher(s).", all.Count);
    }
}
