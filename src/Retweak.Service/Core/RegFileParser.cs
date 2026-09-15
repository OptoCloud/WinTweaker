using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Retweak.Service.Configuration;

namespace Retweak.Service.Core;

/// <summary>
/// Converts a .reg file's text into <see cref="BaselineEntry"/> objects.
///
/// This targets what `regedit /e` actually exports and what hand-written debloat .reg
/// files actually use: "Windows Registry Editor Version 5.00" headers, the dword:/hex:/
/// hex(2):/hex(7):/hex(b): type prefixes, backslash line continuations, and value
/// deletion ("Name"=-). What it does not do, on purpose, is delete an entire key
/// ("[-HKEY...\Key]") — see <see cref="BaselineEntry.EnsureAbsent"/> for why that is out
/// of scope. Constructs it cannot represent are reported as <see cref="Skipped"/> rather
/// than silently dropped or guessed at.
/// </summary>
public static partial class RegFileParser
{
    public readonly record struct SkippedItem(int Line, string Text, string Reason);

    public sealed class Result
    {
        public List<BaselineEntry> Entries { get; } = [];

        public List<SkippedItem> Skipped { get; } = [];
    }

    [GeneratedRegex(@"^\[(?<delete>-)?(?<hive>[A-Za-z_]+)(?<path>\\.*)?\]$")]
    private static partial Regex SectionPattern { get; }

    [GeneratedRegex("""^(?:"(?<name>(?:[^"\\]|\\.)*)"|(?<default>@))=(?<value>.*)$""")]
    private static partial Regex ValuePattern { get; }

    public static Result Parse(TextReader reader)
    {
        var result = new Result();
        string? currentPath = null;
        BaselineScope? currentScope = null;
        var currentSectionUsable = false;

        int lineNumber = 0;
        while (ReadLogicalLine(reader, ref lineNumber) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || IsHeaderLine(trimmed))
            {
                continue;
            }

            var sectionMatch = SectionPattern.Match(trimmed);
            if (sectionMatch.Success)
            {
                if (sectionMatch.Groups["delete"].Success)
                {
                    result.Skipped.Add(new SkippedItem(lineNumber, trimmed,
                        "Whole-key deletion is not supported; see BaselineEntry.EnsureAbsent for why."));
                    currentSectionUsable = false;
                    continue;
                }

                string? currentHive;
                (currentScope, currentHive, currentPath) = MapHive(
                    sectionMatch.Groups["hive"].Value,
                    sectionMatch.Groups["path"].Success ? sectionMatch.Groups["path"].Value.TrimStart('\\') : "");

                if (currentScope is null)
                {
                    result.Skipped.Add(new SkippedItem(lineNumber, trimmed,
                        $"Hive '{currentHive}' has no equivalent Machine/PerUser mapping."));
                    currentSectionUsable = false;
                    continue;
                }

                currentSectionUsable = true;
                continue;
            }

            if (!currentSectionUsable || currentPath is null)
            {
                // Either we have not seen a usable section yet, or the last section was
                // skipped: any value lines under it are meaningless without a target.
                continue;
            }

            var valueMatch = ValuePattern.Match(trimmed);
            if (!valueMatch.Success)
            {
                result.Skipped.Add(new SkippedItem(lineNumber, trimmed, "Line did not parse as a value assignment."));
                continue;
            }

            var name = valueMatch.Groups["default"].Success ? "" : Unescape(valueMatch.Groups["name"].Value);
            var rawValue = valueMatch.Groups["value"].Value.Trim();

            if (rawValue == "-")
            {
                result.Entries.Add(new BaselineEntry
                {
                    Scope = currentScope!.Value,
                    Path = currentPath,
                    Name = name,
                    EnsureAbsent = true,
                });
                continue;
            }

            if (!TryParseTypedValue(rawValue, out RegistryValueKind kind, out string value, out string[] values, out string? error))
            {
                result.Skipped.Add(new SkippedItem(lineNumber, trimmed, error ?? "Unrecognised value syntax."));
                continue;
            }

            result.Entries.Add(new BaselineEntry
            {
                Scope = currentScope!.Value,
                Path = currentPath,
                Name = name,
                Kind = kind,
                Value = value,
                Values = values,
            });
        }

        return result;
    }

    public static Result Parse(string content)
    {
        using var reader = new StringReader(content);
        return Parse(reader);
    }

    /// <summary>
    /// Reads one logical line, joining a trailing backslash continuation (used by hex:/
    /// hex(n): values that wrap across lines) with the next physical line.
    /// </summary>
    private static string? ReadLogicalLine(TextReader reader, ref int lineNumber)
    {
        var first = reader.ReadLine();
        if (first is null)
        {
            return null;
        }

        lineNumber++;
        var sb = new StringBuilder(first);

        while (sb.Length > 0 && sb[^1] == '\\')
        {
            sb.Length--;
            var next = reader.ReadLine();
            if (next is null)
            {
                break;
            }

            lineNumber++;
            sb.Append(next.TrimStart(' ', '\t'));
        }

        return sb.ToString();
    }

    private static bool IsHeaderLine(string trimmed) =>
        trimmed.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a .reg hive token onto Retweak's (Scope, hive-root-relative path) model.
    /// HKCU always becomes PerUser: a .reg file's HKEY_CURRENT_USER only ever wrote to
    /// whoever was logged on at export time, and applying it to every user is what
    /// importing a tweak almost always actually means. HKCR is approximated as the
    /// machine-wide HKLM\SOFTWARE\Classes view; the separate per-user HKCU\SOFTWARE\Classes
    /// override layer is not handled. HKEY_USERS\{SID} and HKEY_CURRENT_CONFIG have no
    /// equivalent and are reported as skipped by the caller.
    /// </summary>
    private static (BaselineScope? Scope, string Hive, string Path) MapHive(string hive, string path)
    {
        return hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => (BaselineScope.PerUser, hive, path),
            "HKEY_LOCAL_MACHINE" or "HKLM" => (BaselineScope.Machine, hive, path),
            "HKEY_CLASSES_ROOT" or "HKCR" => (BaselineScope.Machine, hive,
                path.Length == 0 ? "SOFTWARE\\Classes" : $"SOFTWARE\\Classes\\{path}"),
            _ => (null, hive, path)
        };
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static bool TryParseTypedValue(
        string rawValue,
        out RegistryValueKind kind,
        out string value,
        out string[] values,
        out string? error)
    {
        kind = RegistryValueKind.String;
        value = "";
        values = [];
        error = null;

        if (rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"')
        {
            kind = RegistryValueKind.String;
            value = Unescape(rawValue[1..^1]);
            return true;
        }

        if (rawValue.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            kind = RegistryValueKind.DWord;
            value = "0x" + rawValue["dword:".Length..].Trim();
            return true;
        }

        if (rawValue.StartsWith("hex(b):", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? bytes = TryParseHexBytes(rawValue["hex(b):".Length..]);
            if (bytes is not { Length: 8 })
            {
                error = "hex(b): (QWORD) value must be exactly 8 bytes.";
                return false;
            }

            kind = RegistryValueKind.QWord;
            value = BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (rawValue.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? bytes = TryParseHexBytes(rawValue["hex(2):".Length..]);
            if (bytes is null)
            {
                error = "hex(2): (EXPAND_SZ) value has malformed hex bytes.";
                return false;
            }

            kind = RegistryValueKind.ExpandString;
            value = DecodeUtf16Z(bytes);
            return true;
        }

        if (rawValue.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = TryParseHexBytes(rawValue["hex(7):".Length..]);
            if (bytes is null)
            {
                error = "hex(7): (MULTI_SZ) value has malformed hex bytes.";
                return false;
            }

            kind = RegistryValueKind.MultiString;
            values = DecodeUtf16MultiZ(bytes);
            return true;
        }

        if (rawValue.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = TryParseHexBytes(rawValue["hex:".Length..]);
            if (bytes is null)
            {
                error = "hex: (BINARY) value has malformed hex bytes.";
                return false;
            }

            kind = RegistryValueKind.Binary;
            value = Convert.ToHexString(bytes);
            return true;
        }

        if (rawValue.StartsWith("hex(0):", StringComparison.OrdinalIgnoreCase) ||
            rawValue.StartsWith("hex(", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported hex(n) type code (only 2, 7 and b are handled).";
            return false;
        }

        error = "Value is not a quoted string, dword:, or a recognised hex:/hex(n): form.";
        return false;
    }

    private static byte[]? TryParseHexBytes(string s)
    {
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private static string DecodeUtf16Z(byte[] bytes)
    {
        var s = Encoding.Unicode.GetString(bytes);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static string[] DecodeUtf16MultiZ(byte[] bytes) =>
        [.. Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
}
