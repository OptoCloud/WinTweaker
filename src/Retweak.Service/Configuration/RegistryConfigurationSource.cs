using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Win32;

namespace Retweak.Service.Configuration;

/// <summary>
/// Reads configuration overrides out of a registry key, e.g.
/// HKLM\SOFTWARE\OptoCloud\Retweak\Config.
///
/// Two forms are supported:
///   * Scalar values map directly onto option names:
///       DebounceMilliseconds (REG_DWORD)  -> Retweak:DebounceMilliseconds
///       EventSourceName      (REG_SZ)     -> Retweak:EventSourceName
///       LogLevel             (REG_SZ)     -> Logging:LogLevel:Default
///   * A REG_SZ / REG_MULTI_SZ value named "Json" holds a JSON document that is
///     merged exactly as if it were a second appsettings file. This is how you
///     override the Entries array, because an array does not map onto flat values.
///
/// The provider is deliberately read-once at startup. Registry-driven live reload
/// would need its own watcher and a restart of every other watcher; the service is
/// cheap to restart instead.
/// </summary>
public sealed class RegistryConfigurationSource : IConfigurationSource
{
    public RegistryConfigurationSource(string keyPath, string sectionName, RegistryView view)
    {
        KeyPath = keyPath;
        SectionName = sectionName;
        View = view;
    }

    public string KeyPath { get; }

    public string SectionName { get; }

    public RegistryView View { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new RegistryConfigurationProvider(this);
}

internal sealed class RegistryConfigurationProvider : ConfigurationProvider
{
    private static readonly string[] ScalarNames =
    [
        nameof(RetweakOptions.EventSourceName),
        nameof(RetweakOptions.EventLogName),
        nameof(RetweakOptions.LogDirectory),
        nameof(RetweakOptions.LogFileMaxBytes),
        nameof(RetweakOptions.LogFileRetainCount),
        nameof(RetweakOptions.DebounceMilliseconds),
        nameof(RetweakOptions.AuditIntervalMinutes),
        nameof(RetweakOptions.HiveReconcileSeconds),
        nameof(RetweakOptions.LogonHiveWaitSeconds),
        nameof(RetweakOptions.CaseInsensitiveStringCompare),
        nameof(RetweakOptions.AllowExpandStringEquivalence),
        nameof(RetweakOptions.EnableOfflineProfiles),
    ];

    private readonly RegistryConfigurationSource _source;

    public RegistryConfigurationProvider(RegistryConfigurationSource source) => _source = source;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, _source.View);
            using RegistryKey? key = root.OpenSubKey(_source.KeyPath, writable: false);
            if (key is null)
            {
                Data = data;
                return;
            }

            foreach (string name in ScalarNames)
            {
                object? raw = key.GetValue(name);
                if (raw is null)
                {
                    continue;
                }

                data[$"{_source.SectionName}:{name}"] = Stringify(raw);
            }

            if (key.GetValue("LogLevel") is string level && level.Length > 0)
            {
                data["Logging:LogLevel:Default"] = level;
            }

            if (ReadJson(key) is { Length: > 0 } json)
            {
                MergeJson(json, data);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A missing or unreadable override key is not fatal: appsettings.json wins.
            // Anything else (a malformed JSON blob, for instance) is allowed to throw so
            // that the service fails fast with a visible reason.
        }

        Data = data;
    }

    private static string? ReadJson(RegistryKey key)
    {
        object? raw = key.GetValue("Json");
        return raw switch
        {
            string s => s,
            string[] lines => string.Join('\n', lines),
            _ => null,
        };
    }

    /// <summary>
    /// Flattens a JSON document into configuration keys by reusing the framework's own
    /// JSON provider, so nesting and array indexing behave identically to appsettings.json.
    /// </summary>
    internal static void MergeJson(string json, IDictionary<string, string?> into)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var provider = new JsonStreamConfigurationProvider(new JsonStreamConfigurationSource { Stream = stream });
        provider.Load();

        foreach (string key in EnumerateKeys(provider, parentPath: null))
        {
            if (provider.TryGet(key, out string? value))
            {
                into[key] = value;
            }
        }
    }

    private static IEnumerable<string> EnumerateKeys(IConfigurationProvider provider, string? parentPath)
    {
        foreach (string child in provider.GetChildKeys([], parentPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = parentPath is null ? child : ConfigurationPath.Combine(parentPath, child);
            yield return path;

            foreach (string descendant in EnumerateKeys(provider, path))
            {
                yield return descendant;
            }
        }
    }

    private static string Stringify(object raw) => raw switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string[] m => string.Join(',', m),
        byte[] b => Convert.ToHexString(b),
        _ => raw.ToString() ?? "",
    };
}

public static class RegistryConfigurationExtensions
{
    public static IConfigurationBuilder AddRegistryOverrides(
        this IConfigurationBuilder builder,
        string keyPath,
        string sectionName,
        RegistryView view) =>
        builder.Add(new RegistryConfigurationSource(keyPath, sectionName, view));
}
