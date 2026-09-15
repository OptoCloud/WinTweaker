using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Retweak.Service.Configuration;
using Xunit;

namespace Retweak.Service.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData("0x10", 16)]
    [InlineData("0XFF", 255)]
    [InlineData("4294967295", -1)]
    [InlineData("-1", -1)]
    public void DWordValuesParseToExpectedBits(string configured, int expected)
    {
        var entry = new BaselineEntry { Kind = "DWord", Value = configured };
        Assert.Equal(expected, Assert.IsType<int>(entry.ParseDesiredValue()));
    }

    [Theory]
    [InlineData("0x100000000", 4294967296L)]
    [InlineData("-1", -1L)]
    public void QWordValuesParse(string configured, long expected)
    {
        var entry = new BaselineEntry { Kind = "QWord", Value = configured };
        Assert.Equal(expected, Assert.IsType<long>(entry.ParseDesiredValue()));
    }

    [Theory]
    [InlineData("deadbeef")]
    [InlineData("de ad be ef")]
    [InlineData("de-ad-be-ef")]
    public void BinaryAcceptsSeparators(string configured)
    {
        var entry = new BaselineEntry { Kind = "Binary", Value = configured };
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Assert.IsType<byte[]>(entry.ParseDesiredValue()));
    }

    [Fact]
    public void OddLengthBinaryIsRejected()
    {
        var entry = new BaselineEntry { Kind = "Binary", Value = "abc" };
        Assert.Throws<FormatException>(() => entry.ParseDesiredValue());
    }

    [Fact]
    public void ValidationRejectsAbsolutePaths()
    {
        var entry = new BaselineEntry
        {
            Path = @"HKLM:\SOFTWARE\Foo",
            Name = "Bar",
            Kind = "DWord",
            Value = "1",
        };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("relative", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsPerUserPathContainingSid()
    {
        var entry = new BaselineEntry
        {
            Scope = "PerUser",
            Path = @"S-1-5-21-1-2-3-1001\Software\Foo",
            Name = "Bar",
            Kind = "DWord",
            Value = "1",
        };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("SID", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsUnparseableValue()
    {
        var entry = new BaselineEntry
        {
            Path = @"SOFTWARE\Foo",
            Name = "Bar",
            Kind = "DWord",
            Value = "not-a-number",
        };

        Assert.False(entry.TryValidate(out _));
    }

    [Fact]
    public void ValidEntryPasses()
    {
        var entry = new BaselineEntry
        {
            Scope = "PerUser",
            Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            Name = "HideFileExt",
            Kind = "DWord",
            Value = "0",
        };

        Assert.True(entry.TryValidate(out string error), error);
    }

    [Fact]
    public void EnsureAbsentEntryIgnoresKindAndValue()
    {
        // Kind/Value are left at defaults and would otherwise be nonsense (an empty
        // DWord), but EnsureAbsent means they are never consulted.
        var entry = new BaselineEntry
        {
            Path = @"SOFTWARE\Foo",
            Name = "Bar",
            EnsureAbsent = true,
        };

        Assert.True(entry.TryValidate(out string error), error);
    }

    [Fact]
    public void EnsureAbsentStillRejectsBadPath()
    {
        var entry = new BaselineEntry
        {
            Path = @"HKLM:\SOFTWARE\Foo",
            Name = "Bar",
            EnsureAbsent = true,
        };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("relative", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SectionBindsEntriesAndScopes()
    {
        const string Json = """
        {
          "Retweak": {
            "DebounceMilliseconds": 500,
            "Entries": [
              { "Scope": "Machine", "Path": "SOFTWARE\\A", "Name": "X", "Kind": "DWord", "Value": "1" },
              { "Scope": "PerUser", "Path": "Software\\B", "Name": "Y", "Kind": "String", "Value": "hello" }
            ]
          }
        }
        """;

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json)))
            .Build();

        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()!;

        Assert.Equal(500, options.DebounceMilliseconds);
        Assert.Equal(2, options.Entries.Count);
        Assert.Single(options.MachineEntries);
        Assert.Single(options.PerUserEntries);
        Assert.Equal(RegistryValueKind.String, options.PerUserEntries.First().ResolvedKind);
        Assert.All(options.Entries, e => Assert.True(e.TryValidate(out _)));
    }

    [Fact]
    public void InvalidEnumStringSurvivesBindingAndFailsValidationInstead()
    {
        // Regression test: Kind/Scope/StartType/RunState are bound as raw strings
        // specifically so that a typo like this does not vanish. Before that fix, binding
        // a strongly-typed enum property to an unparseable string caused the configuration
        // binder to silently drop the *entire array element* -- not just the bad property --
        // so a mistyped Kind never reached TryValidate at all: Entries.Count came back one
        // short, with no error, no warning, and no way to detect it short of counting.
        const string Json = """
        {
          "Retweak": {
            "Entries": [
              { "Scope": "Machine", "Path": "SOFTWARE\\A", "Name": "X", "Kind": "NotARealKind", "Value": "1" },
              { "Scope": "Machine", "Path": "SOFTWARE\\B", "Name": "Y", "Kind": "DWord", "Value": "1" }
            ]
          }
        }
        """;

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json)))
            .Build();

        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()!;

        // Both entries must still be present...
        Assert.Equal(2, options.Entries.Count);

        // ...and the bad one must fail validation with a message naming the actual culprit.
        BaselineEntry bad = options.Entries.Single(e => e.Name == "X");
        Assert.False(bad.TryValidate(out string error));
        Assert.Contains("NotARealKind", error, StringComparison.Ordinal);

        BaselineEntry good = options.Entries.Single(e => e.Name == "Y");
        Assert.True(good.TryValidate(out string goodError), goodError);
    }

    [Fact]
    public void InvalidScopeStringFailsValidationRatherThanVanishing()
    {
        const string Json = """
        {
          "Retweak": {
            "Entries": [
              { "Scope": "Machiine", "Path": "SOFTWARE\\A", "Name": "X", "Kind": "DWord", "Value": "1" }
            ]
          }
        }
        """;

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json)))
            .Build();

        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()!;

        BaselineEntry entry = Assert.Single(options.Entries);
        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("Machiine", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidServiceStartTypeStringFailsValidationRatherThanVanishing()
    {
        const string Json = """
        {
          "Retweak": {
            "ServiceEntries": [
              { "Name": "DiagTrack", "StartType": "Disable" }
            ]
          }
        }
        """;

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json)))
            .Build();

        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()!;

        ServiceEntry entry = Assert.Single(options.ServiceEntries);
        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("Disable", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsApplyWhenSectionIsSparse()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream("""{ "Retweak": { } }"""u8.ToArray()))
            .Build();

        // An empty JSON object produces no configuration keys at all, so the binder
        // returns null here rather than a defaulted instance — the same reason
        // Program.cs falls back to `?? new RetweakOptions()` for its boot options.
        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()
            ?? new RetweakOptions();

        Assert.Equal(250, options.DebounceMilliseconds);
        Assert.Equal(60, options.AuditIntervalMinutes);
        Assert.Empty(options.Entries);
        Assert.True(options.CaseInsensitiveStringCompare);
    }

    [Fact]
    public void IntervalsAreClampedToSaneMinimums()
    {
        var options = new RetweakOptions
        {
            AuditIntervalMinutes = 0,
            HiveReconcileSeconds = 0,
            DebounceMilliseconds = -5,
        };

        Assert.Equal(TimeSpan.FromMinutes(1), options.AuditInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HiveReconcileInterval);
        Assert.Equal(TimeSpan.Zero, options.Debounce);
    }

    [Fact]
    public void ShippedAppSettingsBindsAndEveryEntryValidates()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        RetweakOptions options = config.GetSection(RetweakOptions.SectionName).Get<RetweakOptions>()!;

        Assert.NotEmpty(options.Entries);
        Assert.All(options.Entries, e => Assert.True(e.TryValidate(out string error), $"{e.Path}!{e.Name}: {error}"));
        Assert.All(options.ServiceEntries, e => Assert.True(e.TryValidate(out string error), $"{e.Name}: {error}"));
        Assert.All(options.ScheduledTaskEntries, e => Assert.True(e.TryValidate(out string error), $"{e.Path}: {error}"));
    }

    [Fact]
    public void RegistryJsonOverrideFlattensIntoConfigurationKeys()
    {
        const string Json = """
        {
          "Retweak": {
            "AuditIntervalMinutes": 15,
            "Entries": [
              { "Scope": "Machine", "Path": "SOFTWARE\\Override", "Name": "Z", "Kind": "DWord", "Value": "7" }
            ]
          }
        }
        """;

        var flat = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        RegistryConfigurationProvider.MergeJson(Json, flat);

        Assert.Equal("15", flat["Retweak:AuditIntervalMinutes"]);
        Assert.Equal("SOFTWARE\\Override", flat["Retweak:Entries:0:Path"]);
        Assert.Equal("7", flat["Retweak:Entries:0:Value"]);
    }
}
