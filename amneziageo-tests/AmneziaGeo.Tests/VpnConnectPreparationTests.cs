using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class VpnConnectPreparationTests
{
    [Fact]
    public async Task DisconnectCancelsPreparationAndCommandsAlreadyQueued()
    {
        var preparation = new VpnConnectPreparation();
        var finishPreparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        IpcAck Start(string name, CancellationToken ct) => preparation.Apply(() =>
        {
            started.Add(name);
            return new IpcAck(true, "connecting");
        }, ct);

        var first = preparation.RunAsync(async ct =>
        {
            await finishPreparing.Task;
            return Start("first", ct);
        }, ex => throw ex);
        var queued = preparation.RunAsync(ct => Task.FromResult(Start("queued", ct)), ex => throw ex);

        Assert.True(preparation.IsPending);
        var stop = preparation.Disconnect(() => new IpcAck(true, "disconnected"));
        Assert.True(stop.Ack.Ok);
        Assert.False(preparation.IsPending);

        var retry = preparation.RunAsync(ct => Task.FromResult(Start("retry", ct)), ex => throw ex);
        finishPreparing.SetResult();
        Assert.Equal(VpnConnectPreparation.Cancelled, await first);
        Assert.Equal(VpnConnectPreparation.Cancelled, await queued);
        Assert.True((await retry).Ok);
        Assert.Equal(["retry"], started);
    }

    [Fact]
    public async Task ChangingSelectionCancelsTheOldPreparationWithoutDisconnecting()
    {
        var preparation = new VpnConnectPreparation();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = preparation.RunAsync(async ct =>
        {
            await ready.Task;
            return preparation.Apply(() => new IpcAck(true, "old launch"), ct);
        }, ex => throw ex);

        preparation.Invalidate();
        var second = preparation.RunAsync(ct =>
            Task.FromResult(preparation.Apply(() => new IpcAck(true, "new launch"), ct)), ex => throw ex);
        ready.SetResult();

        Assert.Equal(VpnConnectPreparation.Cancelled, await first);
        Assert.Equal("new launch", (await second).Message);
    }

    [Fact]
    public async Task ASecondDisconnectInvalidatesAnAutomaticRestart()
    {
        var preparation = new VpnConnectPreparation();
        var restart = preparation.Disconnect(() => new IpcAck(true, "restarting"));
        preparation.Disconnect(() => new IpcAck(true, "disconnected"));

        var result = await preparation.RunAsync(
            _ => throw new InvalidOperationException("obsolete restart must not prepare"),
            ex => throw ex, restart.Generation);

        Assert.Equal(VpnConnectPreparation.Cancelled, result);
    }

    [Fact]
    public async Task AFailedServiceLaunchReportsTheErrorAndAllowsRetry()
    {
        var preparation = new VpnConnectPreparation();
        var launchError = new InvalidOperationException("start denied");
        Exception? reportedError = null;

        var failed = await preparation.RunAsync(
            ct => Task.FromResult(preparation.Apply<IpcAck>(() => throw launchError, ct)),
            ex =>
            {
                reportedError = ex;
                return new IpcAck(false, "could not start vpn service");
            });

        Assert.False(failed.Ok);
        Assert.Same(launchError, reportedError);
        Assert.False(preparation.IsPending);
        Assert.True((await preparation.RunAsync(
            ct => Task.FromResult(preparation.Apply(() => new IpcAck(true, "connecting"), ct)),
            ex => throw ex)).Ok);
    }
}
