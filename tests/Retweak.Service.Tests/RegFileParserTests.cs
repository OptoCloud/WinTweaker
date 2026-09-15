using Microsoft.Win32;
using Retweak.Service.Configuration;
using Retweak.Service.Core;
using Xunit;

namespace Retweak.Service.Tests;

public class RegFileParserTests
{
    [Fact]
    public void HkcuMapsToPerUserWithHivePrefixStripped()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced]
        "HideFileExt"=dword:00000000
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(BaselineScope.PerUser, entry.ResolvedScope);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", entry.Path);
        Assert.Equal("HideFileExt", entry.Name);
        Assert.Equal(RegistryValueKind.DWord, entry.ResolvedKind);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void HklmMapsToMachine()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent]
        "DisableConsumerFeatures"=dword:00000001
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(BaselineScope.Machine, entry.ResolvedScope);
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", entry.Path);
    }

    [Fact]
    public void HkcrMapsToMachineUnderSoftwareClasses()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_CLASSES_ROOT\.foo]
        @="FooFile"
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(BaselineScope.Machine, entry.ResolvedScope);
        Assert.Equal(@"SOFTWARE\Classes\.foo", entry.Path);
        Assert.Equal("", entry.Name);
        Assert.Equal("FooFile", entry.Value);
    }

    [Theory]
    [InlineData("HKEY_USERS\\S-1-5-20\\Foo")]
    [InlineData("HKEY_CURRENT_CONFIG\\Foo")]
    public void UnmappableHivesAreSkippedNotGuessed(string section)
    {
        string reg = $"""
        Windows Registry Editor Version 5.00

        [{section}]
        "X"=dword:00000001
        """;

        RegFileParser.Result result = RegFileParser.Parse(reg);

        Assert.Empty(result.Entries);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void WholeKeyDeletionIsSkippedNotHonoured()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [-HKEY_CURRENT_USER\Software\SomeDeadKey]
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Empty(result.Entries);
        RegFileParser.SkippedItem skip = Assert.Single(result.Skipped);
        Assert.Contains("Whole-key deletion", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueDeletionBecomesEnsureAbsent()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_CURRENT_USER\Software\Foo]
        "Bar"=-
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.True(entry.EnsureAbsent);
        Assert.Equal("Bar", entry.Name);
        Assert.True(entry.TryValidate(out string error), error);
    }

    [Fact]
    public void DefaultValueNameIsEmptyString()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        @="hello"
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal("", entry.Name);
        Assert.Equal("hello", entry.Value);
    }

    [Fact]
    public void QuotedStringUnescapesBackslashesAndQuotes()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"="C:\\Program Files\\Thing \"quoted\""
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal("C:\\Program Files\\Thing \"quoted\"", entry.Value);
    }

    [Fact]
    public void DwordIsRenderedAsHexAndParsesBackToSameBits()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=dword:000000ff
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.DWord, entry.ResolvedKind);
        Assert.Equal(255, Assert.IsType<int>(entry.ParseDesiredValue()));
    }

    [Fact]
    public void HexBinaryDecodesToUppercaseHexString()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex:de,ad,be,ef
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.Binary, entry.ResolvedKind);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Assert.IsType<byte[]>(entry.ParseDesiredValue()));
    }

    [Fact]
    public void HexExpandStringDecodesUtf16AndTrimsTerminator()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(2):25,00,53,00,79,00,73,00,74,00,65,00,6d,00,52,00,6f,00,6f,00,74,00,25,00,00,00
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.ExpandString, entry.ResolvedKind);
        Assert.Equal("%SystemRoot%", entry.Value);
    }

    [Fact]
    public void HexMultiStringSplitsOnEmbeddedNulls()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(7):61,00,00,00,62,00,00,00,00,00
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.MultiString, entry.ResolvedKind);
        Assert.Equal(["a", "b"], entry.Values);
    }

    [Fact]
    public void HexQwordDecodesLittleEndian()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(b):2a,00,00,00,00,00,00,00
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.QWord, entry.ResolvedKind);
        Assert.Equal(42L, Assert.IsType<long>(entry.ParseDesiredValue()));
    }

    [Fact]
    public void BackslashLineContinuationIsJoinedBeforeParsing()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex:de,ad,be,\
          ef
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Assert.IsType<byte[]>(entry.ParseDesiredValue()));
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        ; a full-line comment
        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]

        ; another comment
        "Bar"=dword:00000001
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Single(result.Entries);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void MalformedValueLineIsSkippedNotThrown()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        this is not a value line
        "Bar"=dword:00000001
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Single(result.Entries);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void UnsupportedHexTypeCodeIsSkipped()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(0):01,02
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Empty(result.Entries);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void HexMultiStringPreservesEmbeddedEmptyString()
    {
        // a, "", b — the middle empty string must survive, not just the terminator.
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(7):61,00,00,00,00,00,62,00,00,00,00,00
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        BaselineEntry entry = Assert.Single(result.Entries);
        Assert.Equal(RegistryValueKind.MultiString, entry.ResolvedKind);
        Assert.Equal(["a", "", "b"], entry.Values);
    }

    [Fact]
    public void HexTypeWithOddByteCountIsSkipped()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Foo]
        "Bar"=hex(2):41,00,42
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Empty(result.Entries);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void RootHiveSectionWithNoSubkeyIsSkipped()
    {
        const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE]
        "Bar"=dword:00000001
        """;

        RegFileParser.Result result = RegFileParser.Parse(Reg);

        Assert.Empty(result.Entries);
        Assert.Single(result.Skipped);
    }
}
