using OpenSstpWin.Core;
using OpenSstpWin.Interop;
using Xunit;

namespace OpenSstpWin.Tests;

/// <summary>
/// Locks down the session lifecycle. The window may only claim "Connected" once
/// the packet pumps are running and the address, DNS servers and routes have been
/// applied machine-wide, and it must notice when the tunnel dies underneath it.
/// </summary>
public class SessionTests
{
    [Fact]
    public async Task SessionKeepsRunningWhileTheTunnelIsAlive()
    {
        using var stop = new CancellationTokenSource();
        var tunnelEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = VpnSession.WaitForStopAsync(tunnelEnd.Task, stop.Token);
        Assert.False(wait.IsCompleted);

        stop.Cancel();
        Assert.False(await wait);
    }

    [Fact]
    public async Task SessionNoticesATunnelThatEndsOnItsOwn()
    {
        using var stop = new CancellationTokenSource();
        var tunnelEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = VpnSession.WaitForStopAsync(tunnelEnd.Task, stop.Token);
        tunnelEnd.SetResult();

        Assert.True(await wait);
    }

    [Fact]
    public async Task SessionReportsAnEndThatHappenedBeforeItStartedWaiting()
    {
        using var stop = new CancellationTokenSource();

        Assert.True(await VpnSession.WaitForStopAsync(Task.CompletedTask, stop.Token));
    }

    [Fact]
    public async Task DisconnectingCompletesTheSignalTheSessionWaitsOn()
    {
        var tunnel = new SstpTunnel(new Settings { Host = "vpn.example.org" }, _ => { });
        var signal = tunnel.Disconnected;
        Assert.False(signal.IsCompleted);

        tunnel.Disconnect();

        // Disconnect() must complete Disconnected, or the session waits forever
        // over a closed tunnel and never cleans the adapter up.
        var finished = await Task.WhenAny(signal, Task.Delay(5000));
        Assert.Same(signal, finished);

        await tunnel.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectingTwiceIsHarmless()
    {
        var tunnel = new SstpTunnel(new Settings { Host = "vpn.example.org" }, _ => { });

        tunnel.Disconnect();
        tunnel.Disconnect();

        Assert.True(tunnel.Disconnected.IsCompleted);
        await tunnel.DisposeAsync();
    }

    [Fact]
    public async Task AnUnknownInterfaceIsNeverAcceptedAsTheRouteWinner()
    {
        Assert.False(await WindowsNetwork.WaitForBestInterfaceAsync("1.1.1.1", 0, TimeSpan.FromSeconds(1)));
    }
}
