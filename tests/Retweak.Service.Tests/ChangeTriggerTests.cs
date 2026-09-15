using Retweak.Service.Infra;
using Xunit;

namespace Retweak.Service.Tests;

public class ChangeTriggerTests
{
    [Fact]
    public async Task WaitAsyncReturnsAfterRequest()
    {
        var trigger = new ChangeTrigger();
        trigger.Request("unit-test");

        string source = await trigger.WaitAsync(CancellationToken.None);

        Assert.Equal("unit-test", source);
    }

    [Fact]
    public async Task MultipleRequestsBeforeWaitCollapseIntoOneSignal()
    {
        var trigger = new ChangeTrigger();

        trigger.Request("first");
        trigger.Request("second");
        trigger.Request("third");

        string source = await trigger.WaitAsync(CancellationToken.None);
        Assert.Equal("third", source);

        // Exactly one signal should have been pending; a second wait must block until
        // another Request arrives.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task RequestAfterConsumptionSignalsAgain()
    {
        var trigger = new ChangeTrigger();

        trigger.Request("first");
        await trigger.WaitAsync(CancellationToken.None);

        trigger.Request("second");
        string source = await trigger.WaitAsync(CancellationToken.None);

        Assert.Equal("second", source);
    }

    [Fact]
    public async Task CancelledWaitDoesNotConsumeSignal()
    {
        var trigger = new ChangeTrigger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // No Request() happened yet, so the wait should observe cancellation, not a signal.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.WaitAsync(cts.Token));
    }
}
