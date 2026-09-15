using System.ServiceProcess;
using Retweak.Service.Configuration;
using Xunit;

namespace Retweak.Service.Tests;

public class ServiceAndTaskConfigurationTests
{
    [Fact]
    public void ServiceEntryRequiresName()
    {
        var entry = new ServiceEntry { Name = "", StartType = "Disabled" };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("Name", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceEntryRequiresAtLeastOneEnforcedProperty()
    {
        var entry = new ServiceEntry { Name = "DiagTrack" };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("nothing to enforce", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceEntryWithOnlyStartTypeIsValid()
    {
        var entry = new ServiceEntry { Name = "DiagTrack", StartType = "Disabled" };

        Assert.True(entry.TryValidate(out string error), error);
    }

    [Fact]
    public void ServiceEntryWithOnlyRunStateIsValid()
    {
        var entry = new ServiceEntry { Name = "DiagTrack", RunState = "Stopped" };

        Assert.True(entry.TryValidate(out string error), error);
    }

    [Fact]
    public void ScheduledTaskEntryRequiresPath()
    {
        var entry = new ScheduledTaskEntry { Path = "", Enabled = false };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains("Path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScheduledTaskEntryRequiresFullPath()
    {
        var entry = new ScheduledTaskEntry { Path = "Microsoft Compatibility Appraiser", Enabled = false };

        Assert.False(entry.TryValidate(out string error));
        Assert.Contains(@"\", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTaskEntryWithFullPathIsValid()
    {
        var entry = new ScheduledTaskEntry
        {
            Path = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            Enabled = false,
        };

        Assert.True(entry.TryValidate(out string error), error);
    }
}
