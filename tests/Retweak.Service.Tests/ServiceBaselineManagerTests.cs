using System.ServiceProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Retweak.Service.Configuration;
using Retweak.Service.Core;
using Xunit;

namespace Retweak.Service.Tests;

public class ServiceBaselineManagerTests
{
    [Fact]
    public void ApplyBaselineWithNoEntriesDoesNothingAndDoesNotThrow()
    {
        // With an empty ServiceEntries list the foreach body never runs, so this never
        // touches a live service or the SCM - safe to run in any environment.
        var manager = new ServiceBaselineManager(
            Options.Create(new RetweakOptions()),
            NullLogger<ServiceBaselineManager>.Instance);

        PassReport report = manager.ApplyBaseline("test");

        Assert.Equal(0, report.Checked);
        Assert.Equal(0, report.Repaired);
        Assert.Equal(0, report.Failed);
    }

    [Theory]
    [InlineData(4, ServiceStartMode.Disabled, true)]
    [InlineData(2, ServiceStartMode.Automatic, true)]
    [InlineData(3, ServiceStartMode.Manual, true)]
    [InlineData(2, ServiceStartMode.Disabled, false)]
    [InlineData(4, ServiceStartMode.Manual, false)]
    public void StartTypeComplianceComparesRegistryDwordAgainstDesiredMode(int actual, ServiceStartMode desired, bool expected)
    {
        Assert.Equal(expected, ServiceBaselineManager.IsStartTypeCompliant(actual, desired));
    }

    [Fact]
    public void StartTypeIsNotCompliantWhenValueIsAbsent()
    {
        Assert.False(ServiceBaselineManager.IsStartTypeCompliant(null, ServiceStartMode.Disabled));
    }

    [Fact]
    public void StartTypeIsNotCompliantWhenValueIsWrongClrType()
    {
        // A REG_SZ or REG_BINARY Start value would come back as a non-int object;
        // treated as non-compliant rather than throwing.
        Assert.False(ServiceBaselineManager.IsStartTypeCompliant("4", ServiceStartMode.Disabled));
    }

    [Theory]
    [InlineData(1, ServiceStartMode.Automatic, false)] // delayed-auto flag set: not plain Automatic
    [InlineData(0, ServiceStartMode.Automatic, true)]
    [InlineData(null, ServiceStartMode.Automatic, true)] // value absent = not delayed
    [InlineData(1, ServiceStartMode.Manual, true)] // flag is meaningless outside Automatic
    [InlineData(1, ServiceStartMode.Disabled, true)]
    public void DelayedAutostartComplianceOnlyMattersForAutomatic(object? actual, ServiceStartMode desired, bool expected)
    {
        Assert.Equal(expected, ServiceBaselineManager.IsDelayedAutostartCompliant(actual, desired));
    }

    [Theory]
    [InlineData(ServiceControllerStatus.Running, DesiredServiceRunState.Running, true)]
    [InlineData(ServiceControllerStatus.Stopped, DesiredServiceRunState.Stopped, true)]
    [InlineData(ServiceControllerStatus.Stopped, DesiredServiceRunState.Running, false)]
    [InlineData(ServiceControllerStatus.Running, DesiredServiceRunState.Stopped, false)]
    [InlineData(ServiceControllerStatus.StartPending, DesiredServiceRunState.Running, false)]
    [InlineData(ServiceControllerStatus.StopPending, DesiredServiceRunState.Stopped, false)]
    [InlineData(ServiceControllerStatus.Paused, DesiredServiceRunState.Running, false)]
    public void RunStateComplianceRequiresTheExactStatus(
        ServiceControllerStatus actual, DesiredServiceRunState desired, bool expected)
    {
        Assert.Equal(expected, ServiceBaselineManager.IsRunStateCompliant(actual, desired));
    }
}
