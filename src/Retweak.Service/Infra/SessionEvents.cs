using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Retweak.Service.Infra;

public enum SessionEventKind
{
    Logon,
    Logoff,
}

public readonly record struct SessionEvent(SessionEventKind Kind, int SessionId, string? Sid);

/// <summary>
/// Decouples the service lifetime (which receives SCM callbacks on an SCM thread that must
/// not block) from the worker that reacts to them.
/// </summary>
public sealed class SessionEventBus
{
    private readonly Channel<SessionEvent> _channel =
        Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public ChannelReader<SessionEvent> Reader => _channel.Reader;

    public void Publish(SessionEvent evt) => _channel.Writer.TryWrite(evt);
}

[SupportedOSPlatform("windows")]
public static class SessionSid
{
    /// <summary>
    /// Resolves a session id to the user's SID via WTSQueryUserToken. Requires SeTcbPrivilege,
    /// which LocalSystem has. Returns null for sessions with no interactive user token,
    /// which is normal for the services session and for a session mid-teardown.
    ///
    /// This is a convenience for targeting a logon quickly. It is never load-bearing:
    /// if it fails, hive reconciliation still finds the user.
    /// </summary>
    public static string? TryResolve(int sessionId, ILogger log)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out token) || token == IntPtr.Zero)
            {
                log.LogDebug("WTSQueryUserToken failed for session {Session}; falling back to hive reconciliation.", sessionId);
                return null;
            }

            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not resolve SID for session {Session}.", sessionId);
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }
}

/// <summary>
/// WindowsServiceLifetime with session change notifications enabled.
///
/// CanStop is left at its default (true) on purpose; see README. Set
/// <see cref="ServiceControlOptions.RefuseStop"/> if you really want the
/// unstoppable behaviour, and read the caveats first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionAwareLifetime : WindowsServiceLifetime
{
    private readonly SessionEventBus _bus;
    private readonly ILogger<SessionAwareLifetime> _log;
    private readonly WatcherManager _watchers;

    public SessionAwareLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        IOptions<ServiceControlOptions> controlOptions,
        SessionEventBus bus,
        WatcherManager watchers)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _bus = bus;
        _watchers = watchers;
        _log = loggerFactory.CreateLogger<SessionAwareLifetime>();

        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        CanStop = !controlOptions.Value.RefuseStop;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        SessionEventKind? kind = changeDescription.Reason switch
        {
            SessionChangeReason.SessionLogon => SessionEventKind.Logon,
            SessionChangeReason.SessionUnlock => SessionEventKind.Logon,
            SessionChangeReason.RemoteConnect => SessionEventKind.Logon,
            SessionChangeReason.SessionLogoff => SessionEventKind.Logoff,
            _ => null,
        };

        if (kind is null)
        {
            return;
        }

        int sessionId = changeDescription.SessionId;

        // Resolve on a pool thread: the SCM expects this callback to return promptly and
        // token queries can block briefly during logon.
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var sid = kind == SessionEventKind.Logoff
                ? null
                : SessionSid.TryResolve(sessionId, _log);

            _log.LogDebug("Session {Session} {Kind} (sid={Sid}).", sessionId, kind, sid ?? "unresolved");
            _bus.Publish(new SessionEvent(kind.Value, sessionId, sid));
        }, null);
    }

    protected override void OnShutdown()
    {
        _log.LogInformation("System shutdown: releasing watchers.");
        _watchers.Dispose();
        base.OnShutdown();
    }
}

/// <summary>Controls whether the service accepts a manual STOP.</summary>
public sealed class ServiceControlOptions
{
    public const string SectionName = "ServiceControl";

    /// <summary>
    /// When true, CanStop is false and 'sc stop' is rejected. Off by default because it
    /// mainly obstructs the administrator, not a determined process; see README.
    /// </summary>
    public bool RefuseStop { get; set; }
}
