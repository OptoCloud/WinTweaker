using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Retweak.Service.Configuration;
using Retweak.Service.Core;
using Xunit;

namespace Retweak.Service.Tests;

public class ScheduledTaskBaselineManagerTests
{
    [Fact]
    public void ApplyBaselineWithNoEntriesDoesNothingAndDoesNotThrow()
    {
        // With an empty ScheduledTaskEntries list, ApplyBaseline short-circuits before
        // ever constructing a TaskService - safe to run in any environment, including
        // one without the Task Scheduler service available.
        var manager = new ScheduledTaskBaselineManager(
            Options.Create(new RetweakOptions()),
            NullLogger<ScheduledTaskBaselineManager>.Instance);

        PassReport report = manager.ApplyBaseline("test");

        Assert.Equal(0, report.Checked);
        Assert.Equal(0, report.Repaired);
        Assert.Equal(0, report.Failed);
    }
}
