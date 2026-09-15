namespace Retweak.Service.Infra;

/// <summary>
/// A one-slot signal. Any number of change notifications arriving while a pass is pending
/// collapse into a single wake-up, and the most recent source label wins for logging.
///
/// This is the coalescing half of the debounce; the other half is that
/// <see cref="Core.BaselineManager"/> only writes when a value actually differs, so an
/// echo of our own write costs one comparison and produces no log line and no new write.
/// Time-based suppression alone would be wrong here: a real change landing inside the
/// suppression window would be dropped entirely.
/// </summary>
public sealed class ChangeTrigger : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private string _lastSource = "unknown";

    public void Dispose() => _signal.Dispose();

    public void Request(string source)
    {
        Volatile.Write(ref _lastSource, source);

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending. That is the whole point.
        }
    }

    public async Task<string> WaitAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _lastSource);
    }
}
