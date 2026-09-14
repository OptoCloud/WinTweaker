using Microsoft.Win32;
using Retweak.Service.Core;
using Xunit;

namespace Retweak.Service.Tests;

public class NormalizationTests
{
    private static readonly NormOptions Default = NormOptions.Default;
    private static readonly NormOptions Strict = new(CaseInsensitiveStrings: false, AllowExpandStringEquivalence: false);

    [Fact]
    public void AbsentValueIsNeverCompliant()
    {
        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, 1, RegistryValueKind.Unknown, null, Default));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    public void DWordComparesByValue(int desired, int actual, bool expected)
    {
        Assert.Equal(expected, RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, desired, RegistryValueKind.DWord, actual, Default));
    }

    [Fact]
    public void DWordAcceptsStringStoredValue()
    {
        // A REG_SZ "1" where a REG_DWORD 1 was wanted expresses the same intent.
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, 1, RegistryValueKind.String, "1", Default));

        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, 1, RegistryValueKind.String, "0x1", Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, 1, RegistryValueKind.String, "banana", Default));
    }

    [Fact]
    public void DWordTreatsNegativeAndUnsignedAsSameBits()
    {
        // -1 and 0xFFFFFFFF are the same four bytes; the registry has no notion of sign.
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.DWord, -1, RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF), Default));
    }

    [Fact]
    public void DWordDoesNotMatchOnTruncatedQWordBits()
    {
        // 0x1_0000_0001 truncated to 32 bits is 1, but a QWord source is a different value.
        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.QWord, 0x1_0000_0001L, RegistryValueKind.QWord, 1L, Default));
    }

    [Fact]
    public void QWordComparesFullWidth()
    {
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.QWord, 8_589_934_592L, RegistryValueKind.QWord, 8_589_934_592L, Default));
    }

    [Fact]
    public void StringComparisonHonoursCaseOption()
    {
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.String, "Enabled", RegistryValueKind.String, "enabled", Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.String, "Enabled", RegistryValueKind.String, "enabled", Strict));
    }

    [Fact]
    public void ExpandStringEquivalenceIsOptional()
    {
        string expanded = Environment.ExpandEnvironmentVariables("%SystemRoot%");

        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.ExpandString, "%SystemRoot%", RegistryValueKind.String, expanded, Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.ExpandString, "%SystemRoot%", RegistryValueKind.String, expanded, Strict));
    }

    [Fact]
    public void PlainStringsAreNotExpandedAgainstEachOther()
    {
        // Neither side is an expand-string, so a literal %SystemRoot% stays literal.
        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.String,
            "%SystemRoot%",
            RegistryValueKind.String,
            Environment.ExpandEnvironmentVariables("%SystemRoot%"),
            Default));
    }

    [Fact]
    public void MultiStringIsOrderSensitive()
    {
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.MultiString,
            new[] { "a", "b" },
            RegistryValueKind.MultiString,
            new[] { "a", "b" },
            Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.MultiString,
            new[] { "a", "b" },
            RegistryValueKind.MultiString,
            new[] { "b", "a" },
            Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.MultiString,
            new[] { "a", "b" },
            RegistryValueKind.MultiString,
            new[] { "a" },
            Default));
    }

    [Fact]
    public void BinaryComparesBytes()
    {
        Assert.True(RegistryHelpers.EqualsNorm(
            RegistryValueKind.Binary,
            new byte[] { 0xDE, 0xAD },
            RegistryValueKind.Binary,
            new byte[] { 0xDE, 0xAD },
            Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.Binary,
            new byte[] { 0xDE, 0xAD },
            RegistryValueKind.Binary,
            new byte[] { 0xDE, 0xAF },
            Default));
    }

    [Fact]
    public void MismatchedShapesAreNotCompliant()
    {
        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.String, "x", RegistryValueKind.MultiString, new[] { "x" }, Default));

        Assert.False(RegistryHelpers.EqualsNorm(
            RegistryValueKind.MultiString, new[] { "x" }, RegistryValueKind.String, "x", Default));
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("0x2A", 42L)]
    [InlineData("  42  ", 42L)]
    [InlineData("-1", -1L)]
    public void IntegerCoercionAcceptsCommonForms(string input, long expected)
    {
        Assert.True(RegistryHelpers.TryCoerceInteger(input, out long actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("0xZZ")]
    public void IntegerCoercionRejectsGarbage(string input)
    {
        Assert.False(RegistryHelpers.TryCoerceInteger(input, out _));
    }
}
