using System.Globalization;
using Microsoft.Win32;

namespace Retweak.Service.Configuration;

public enum BaselineScope
{
    /// <summary>Enforced once under HKEY_LOCAL_MACHINE.</summary>
    Machine,

    /// <summary>Enforced once per loaded user hive under HKEY_USERS\{SID}.</summary>
    PerUser,
}

/// <summary>
/// One (hive, path, name, kind, value) tuple to keep enforced.
/// </summary>
public sealed class BaselineEntry
{
    public BaselineScope Scope { get; set; } = BaselineScope.Machine;

    /// <summary>
    /// Sub-key path relative to the hive root. For <see cref="BaselineScope.PerUser"/>
    /// this is relative to HKU\{SID}, i.e. do NOT include the SID.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Value name. Empty string means the key's default value.</summary>
    public string Name { get; set; } = "";

    public RegistryValueKind Kind { get; set; } = RegistryValueKind.DWord;

    /// <summary>
    /// Desired value in string form. DWord/QWord accept decimal or 0x-prefixed hex.
    /// Binary accepts hex ("0a1b2c" or "0a 1b 2c"). Ignored for MultiString.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>Desired value for MultiString entries.</summary>
    public string[] Values { get; set; } = [];

    /// <summary>
    /// Install a change watcher for this entry's key. Set false for values you only
    /// want repaired by the periodic audit (e.g. keys that churn constantly).
    /// </summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// Stable identity used in logs and in the "already reported" drift set.
    /// </summary>
    public string DescribeAt(string scopeTag) =>
        $"{scopeTag}\\{Path}!{(Name.Length == 0 ? "(Default)" : Name)}";

    /// <summary>
    /// Materialises <see cref="Value"/>/<see cref="Values"/> into the CLR type that
    /// <see cref="RegistryKey.SetValue(string, object, RegistryValueKind)"/> expects.
    /// Throws <see cref="FormatException"/> on malformed configuration so that a bad
    /// config fails loudly at startup rather than silently enforcing garbage.
    /// </summary>
    public object ParseDesiredValue()
    {
        switch (Kind)
        {
            case RegistryValueKind.DWord:
                return unchecked((int)ParseUInt32(Value));

            case RegistryValueKind.QWord:
                return unchecked((long)ParseUInt64(Value));

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return Value;

            case RegistryValueKind.MultiString:
                return Values;

            case RegistryValueKind.Binary:
                return ParseHex(Value);

            default:
                throw new FormatException(
                    $"Unsupported registry value kind '{Kind}' for {Path}!{Name}.");
        }
    }

    /// <summary>
    /// Validates the entry, returning a human-readable reason when it is unusable.
    /// </summary>
    public bool TryValidate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            error = "Path is required.";
            return false;
        }

        if (Path.StartsWith('\\') || Path.Contains(":\\", StringComparison.Ordinal))
        {
            error = $"Path '{Path}' must be relative to the hive root (no leading backslash, no 'HKLM:').";
            return false;
        }

        if (Scope == BaselineScope.PerUser &&
            Path.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            error = $"PerUser path '{Path}' must not include the SID; it is prefixed automatically.";
            return false;
        }

        try
        {
            _ = ParseDesiredValue();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = "";
        return true;
    }

    private static uint ParseUInt32(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            throw new FormatException("DWord value is empty.");
        }

        // Accept both signed input (-1) and unsigned/hex (0xFFFFFFFF) for the same bits.
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (s[0] == '-')
        {
            return unchecked((uint)int.Parse(s, CultureInfo.InvariantCulture));
        }

        return uint.Parse(s, CultureInfo.InvariantCulture);
    }

    private static ulong ParseUInt64(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            throw new FormatException("QWord value is empty.");
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (s[0] == '-')
        {
            return unchecked((ulong)long.Parse(s, CultureInfo.InvariantCulture));
        }

        return ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    private static byte[] ParseHex(string s)
    {
        ReadOnlySpan<char> src = s.AsSpan().Trim();
        Span<char> packed = src.Length <= 256 ? stackalloc char[src.Length] : new char[src.Length];
        int n = 0;
        foreach (char c in src)
        {
            if (c is ' ' or '-' or ':' or ',')
            {
                continue;
            }

            packed[n++] = c;
        }

        if ((n & 1) != 0)
        {
            throw new FormatException("Binary value must contain an even number of hex digits.");
        }

        var bytes = new byte[n / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(packed.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}

public sealed class RetweakOptions
{
    public const string SectionName = "Retweak";

    /// <summary>Event Log source name. Created by the installer, not at runtime.</summary>
    public string EventSourceName { get; set; } = "Retweak";

    /// <summary>Event Log log name to write into.</summary>
    public string EventLogName { get; set; } = "Application";

    /// <summary>
    /// Directory for the fallback rolling file log. Environment variables are expanded.
    /// Note this expands in LocalSystem's environment, so avoid %USERPROFILE%.
    /// </summary>
    public string LogDirectory { get; set; } = @"%ProgramData%\Retweak\logs";

    public int LogFileMaxBytes { get; set; } = 4 * 1024 * 1024;

    public int LogFileRetainCount { get; set; } = 5;

    /// <summary>
    /// How long to wait after a change notification before running a pass, so that a
    /// burst of related writes collapses into one apply. Also the window during which
    /// notifications following our own writes are not logged as user-visible drift.
    /// </summary>
    public int DebounceMilliseconds { get; set; } = 250;

    /// <summary>Periodic full sweep that catches anything the watchers missed.</summary>
    public int AuditIntervalMinutes { get; set; } = 60;

    /// <summary>How often to reconcile the set of loaded user hives against watchers.</summary>
    public int HiveReconcileSeconds { get; set; } = 60;

    /// <summary>
    /// After a logon, how long to keep retrying for the user's hive to appear in HKU.
    /// Profile load is not synchronous with the session-change notification.
    /// </summary>
    public int LogonHiveWaitSeconds { get; set; } = 60;

    /// <summary>Compare String/ExpandString values case-insensitively.</summary>
    public bool CaseInsensitiveStringCompare { get; set; } = true;

    /// <summary>
    /// Treat a REG_EXPAND_SZ whose expansion matches the desired REG_SZ (and vice versa)
    /// as compliant instead of rewriting it on every pass.
    /// </summary>
    public bool AllowExpandStringEquivalence { get; set; } = true;

    /// <summary>
    /// Reserved for the offline-profile mode (RegLoadKey over ProfileList). Off by default;
    /// see README for why this is deliberately not implemented as a silent default.
    /// </summary>
    public bool EnableOfflineProfiles { get; set; }

    public List<BaselineEntry> Entries { get; set; } = [];

    public IEnumerable<BaselineEntry> MachineEntries =>
        Entries.Where(e => e.Scope == BaselineScope.Machine);

    public IEnumerable<BaselineEntry> PerUserEntries =>
        Entries.Where(e => e.Scope == BaselineScope.PerUser);

    public TimeSpan Debounce => TimeSpan.FromMilliseconds(Math.Max(0, DebounceMilliseconds));

    public TimeSpan AuditInterval => TimeSpan.FromMinutes(Math.Max(1, AuditIntervalMinutes));

    public TimeSpan HiveReconcileInterval => TimeSpan.FromSeconds(Math.Max(5, HiveReconcileSeconds));
}
