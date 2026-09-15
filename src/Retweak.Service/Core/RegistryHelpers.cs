using System.Globalization;
using Microsoft.Win32;

namespace Retweak.Service.Core;

public readonly record struct NormOptions(
    bool CaseInsensitiveStrings,
    bool AllowExpandStringEquivalence)
{
    public static NormOptions Default => new(CaseInsensitiveStrings: true, AllowExpandStringEquivalence: true);
}

public static class RegistryHelpers
{
    /// <summary>Longest value rendered by <see cref="Describe"/> before it is truncated with an ellipsis.</summary>
    private const int MaxDescribeLength = 120;

    /// <summary>
    /// The view to use for every registry operation. On a 64-bit OS we always want the
    /// native 64-bit view; on a 32-bit OS Registry64 is meaningless and Registry32 is the
    /// only correct choice. Computed once because it cannot change at runtime.
    /// </summary>
    public static RegistryView NativeView { get; } =
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

    public static bool UseWow64_64Key => Environment.Is64BitOperatingSystem;

    /// <summary>
    /// Compares an actual registry value against a desired one, tolerating the type
    /// sloppiness that real registries are full of: a DWORD written as REG_SZ "1",
    /// a REG_EXPAND_SZ whose expansion matches a plain string, boxed uint vs int,
    /// and so on.
    /// </summary>
    /// <param name="desiredKind">Configured kind.</param>
    /// <param name="desiredValue">Result of <c>BaselineEntry.ParseDesiredValue()</c>.</param>
    /// <param name="actualKind">
    /// Kind reported by <c>RegistryKey.GetValueKind</c>, or <see cref="RegistryValueKind.Unknown"/>
    /// when the value is absent.
    /// </param>
    /// <param name="actualValue">
    /// Value read with <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>, or null
    /// when absent.
    /// </param>
    /// <param name="options"></param>
    public static bool EqualsNorm(
        RegistryValueKind desiredKind,
        object desiredValue,
        RegistryValueKind actualKind,
        object? actualValue,
        NormOptions options)
    {
        if (actualValue is null || actualKind == RegistryValueKind.Unknown || actualKind == RegistryValueKind.None)
        {
            return false;
        }

        switch (desiredKind)
        {
            case RegistryValueKind.DWord:
            case RegistryValueKind.QWord:
            {
                if (!TryCoerceInteger(desiredValue, out long want) ||
                    !TryCoerceInteger(actualValue, out long got))
                {
                    return false;
                }

                // A DWORD stored as REG_SZ "1" is the same intent as REG_DWORD 1, but the
                // widths differ: compare the meaningful bits only.
                if (desiredKind == RegistryValueKind.DWord)
                {
                    return unchecked((uint)want) == unchecked((uint)got);
                }

                return want == got;
            }

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            {
                if (actualKind is RegistryValueKind.MultiString or RegistryValueKind.Binary)
                {
                    return false;
                }

                // Kind mismatch between SZ and EXPAND_SZ is only forgiven when asked for.
                bool kindDiffers = actualKind != desiredKind;
                if (kindDiffers &&
                    !(options.AllowExpandStringEquivalence &&
                      actualKind is RegistryValueKind.String or RegistryValueKind.ExpandString))
                {
                    return false;
                }

                string want = Stringify(desiredValue);
                string got = Stringify(actualValue);
                StringComparison cmp = options.CaseInsensitiveStrings
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (string.Equals(want, got, cmp))
                {
                    return true;
                }

                if (!options.AllowExpandStringEquivalence)
                {
                    return false;
                }

                // Expansion happens in the service account's environment, which is not the
                // logged-on user's. Only compare expansions when at least one side is an
                // expand-string, so a literal "%FOO%" desired as REG_SZ stays literal.
                bool expandable = desiredKind == RegistryValueKind.ExpandString ||
                                  actualKind == RegistryValueKind.ExpandString;
                if (!expandable)
                {
                    return false;
                }

                return string.Equals(Expand(want), Expand(got), cmp);
            }

            case RegistryValueKind.MultiString:
            {
                if (actualValue is not string[] got || desiredValue is not string[] want)
                {
                    return false;
                }

                if (actualKind != RegistryValueKind.MultiString || got.Length != want.Length)
                {
                    return false;
                }

                StringComparison cmp = options.CaseInsensitiveStrings
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                for (int i = 0; i < want.Length; i++)
                {
                    if (!string.Equals(want[i], got[i], cmp))
                    {
                        return false;
                    }
                }

                return true;
            }

            case RegistryValueKind.Binary:
            {
                if (actualKind != RegistryValueKind.Binary ||
                    desiredValue is not byte[] want ||
                    actualValue is not byte[] got)
                {
                    return false;
                }

                return want.AsSpan().SequenceEqual(got);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Accepts int, uint, long, ulong and decimal/hex strings. Returns false for anything
    /// that is not plausibly an integer, which is treated as non-compliant rather than
    /// throwing, so one odd value cannot abort a whole pass.
    /// </summary>
    internal static bool TryCoerceInteger(object value, out long result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case uint u:
                result = u;
                return true;
            case long l:
                result = l;
                return true;
            case ulong ul:
                result = unchecked((long)ul);
                return true;
            case byte[] { Length: 4 or 8 } b:
                // REG_BINARY holding a little-endian integer.
                result = b.Length == 4
                    ? BitConverter.ToUInt32(b)
                    : BitConverter.ToInt64(b);
                return true;
            case string s:
            {
                s = s.Trim();
                if (s.Length == 0)
                {
                    result = 0;
                    return false;
                }

                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex))
                    {
                        result = unchecked((long)hex);
                        return true;
                    }

                    result = 0;
                    return false;
                }

                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dec))
                {
                    result = dec;
                    return true;
                }

                if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong udec))
                {
                    result = unchecked((long)udec);
                    return true;
                }

                result = 0;
                return false;
            }

            default:
                result = 0;
                return false;
        }
    }

    internal static string Stringify(object value) => value switch
    {
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string[] m => string.Join('\0', m),
        byte[] b => Convert.ToHexString(b),
        _ => value.ToString() ?? "",
    };

    private static string Expand(string s)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(s);
        }
        catch
        {
            return s;
        }
    }

    /// <summary>Short, log-safe rendering of a value that may be null or a byte array.</summary>
    public static string Describe(object? value)
    {
        if (value is null)
        {
            return "(absent)";
        }

        string s = Stringify(value);
        return s.Length <= MaxDescribeLength
            ? s
            : string.Concat(s.AsSpan(0, MaxDescribeLength - 3), "...");
    }
}
