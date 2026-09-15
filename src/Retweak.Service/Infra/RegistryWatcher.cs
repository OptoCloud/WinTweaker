using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Retweak.Service.Infra;

/// <summary>
/// Watches a single registry key for value changes using RegNotifyChangeKeyValue in
/// asynchronous mode, re-arming after every signal so the watch lives as long as the
/// object does.
///
/// Design points worth knowing:
///  * The key handle must stay open for the lifetime of the watch. Closing it cancels
///    the pending notification, which is also how Dispose unblocks a waiting event.
///  * REG_NOTIFY_THREAD_AGNOSTIC is mandatory here; see NativeMethods for why.
///  * ThreadPool.RegisterWaitForSingleObject is used instead of a dedicated thread, so
///    fifty watchers cost fifty handles and no threads, not fifty threads.
///  * subtree = false keeps the notification lightweight, but it means a change to a
///    value under a child key is invisible. Watch each key you enforce.
/// </summary>
internal sealed class RegistryWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<RegistryWatcher> _onChanged;
    private readonly ILogger _log;
    private readonly int _filter;

    private IntPtr _hKey;
    private ManualResetEvent? _signal;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private RegistryWatcher(
        IntPtr hKey,
        string description,
        int filter,
        Action<RegistryWatcher> onChanged,
        ILogger log)
    {
        _hKey = hKey;
        Description = description;
        _filter = filter;
        _onChanged = onChanged;
        _log = log;
        _signal = new ManualResetEvent(initialState: false);
    }

    /// <summary>Human-readable key path, used for logging and for the watcher map.</summary>
    public string Description { get; }

    /// <summary>
    /// Arbitrary tag the owner can use to correlate the watcher back to a SID.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Opens the key and arms the first notification. Returns null (with a logged reason)
    /// when the key does not exist or cannot be opened for notification, which is an
    /// ordinary situation: policy keys often do not exist until something creates them.
    /// </summary>
    public static RegistryWatcher? TryCreate(
        IntPtr rootHive,
        string subKey,
        bool wow64_64,
        string description,
        Action<RegistryWatcher> onChanged,
        ILogger log,
        string? tag = null)
    {
        int sam = NativeMethods.KEY_NOTIFY |
                  (wow64_64 ? NativeMethods.KEY_WOW64_64KEY : NativeMethods.KEY_WOW64_32KEY);

        int rc = NativeMethods.RegOpenKeyEx(rootHive, subKey, 0, sam, out IntPtr hKey);
        if (rc != NativeMethods.ERROR_SUCCESS)
        {
            if (rc == NativeMethods.ERROR_FILE_NOT_FOUND)
            {
                log.LogDebug("No watcher for {Key}: key does not exist yet.", description);
            }
            else
            {
                log.LogWarning(
                    new Win32Exception(rc),
                    "RegOpenKeyEx failed for {Key} (rc={Rc}); this key will be covered by the periodic audit only.",
                    description,
                    rc);
            }

            return null;
        }

        const int Filter = NativeMethods.REG_NOTIFY_CHANGE_LAST_SET |
                           NativeMethods.REG_NOTIFY_THREAD_AGNOSTIC;

        var watcher = new RegistryWatcher(hKey, description, Filter, onChanged, log) { Tag = tag };
        if (!watcher.Arm())
        {
            watcher.Dispose();
            return null;
        }

        log.LogDebug("Watching {Key}.", description);
        return watcher;
    }

    /// <summary>
    /// Resets the event, re-registers the notification, then re-registers the pooled wait.
    /// Order matters: the reset must precede the arm, otherwise a stale signal from the
    /// previous notification would fire the callback immediately. There is no window in
    /// which a change can be missed, because a disarmed notification cannot signal.
    /// </summary>
    private bool Arm()
    {
        lock (_gate)
        {
            if (_disposed || _signal is null)
            {
                return false;
            }

            _signal.Reset();

            int rc = NativeMethods.RegNotifyChangeKeyValue(
                _hKey,
                bWatchSubtree: false,
                _filter,
                _signal.SafeWaitHandle,
                fAsynchronous: true);

            if (rc != NativeMethods.ERROR_SUCCESS)
            {
                _log.LogWarning(
                    new Win32Exception(rc),
                    "RegNotifyChangeKeyValue failed for {Key} (rc={Rc}); watcher stopping, audit will still cover it.",
                    Description,
                    rc);
                return false;
            }

            _wait = ThreadPool.RegisterWaitForSingleObject(
                _signal,
                static (state, _) => ((RegistryWatcher)state!).OnSignal(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);

            return true;
        }
    }

    private void OnSignal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Safe from the callback thread specifically because no wait handle is passed.
            _wait?.Unregister(null);
            _wait = null;
        }

        try
        {
            _onChanged(this);
        }
        catch (Exception ex)
        {
            // A failure to handle one notification must never take the watcher down.
            _log.LogError(ex, "Change handler threw for {Key}.", Description);
        }

        if (!Arm() && !_disposed)
        {
            _log.LogWarning("Watcher for {Key} could not re-arm and is now inactive.", Description);
        }
    }

    public void Dispose()
    {
        ManualResetEvent? signal;
        IntPtr hKey;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wait?.Unregister(null);
            _wait = null;

            signal = _signal;
            _signal = null;
            hKey = _hKey;
            _hKey = IntPtr.Zero;
        }

        if (hKey != IntPtr.Zero)
        {
            // Closing the key cancels any pending notification and signals the event,
            // which is why _disposed is checked at the top of OnSignal. The return code
            // is not actionable here: Dispose has no way to retry or report a failure.
            int rc = NativeMethods.RegCloseKey(hKey);
            Debug.Assert(rc == NativeMethods.ERROR_SUCCESS, $"RegCloseKey failed with {rc}.");
        }

        // The event is intentionally not disposed here. Unregister(null) does not wait for
        // an in-flight callback, so an explicit Dispose could race a pooled thread still
        // touching the handle. ManualResetEvent owns a finalizable SafeWaitHandle, so the
        // handle is released once the last reference goes away; leaking it briefly is
        // strictly better than an ObjectDisposedException on a background thread.
        GC.KeepAlive(signal);
    }
}
