using System.Net;
using OpenSstpWin.Interop;

namespace OpenSstpWin.Core;

/// <summary>A route the session installed, so it can be removed again.</summary>
internal sealed record InstalledRoute(uint InterfaceIndex, string Prefix);

/// <summary>Live packet counters, shown in the window to prove traffic is flowing.</summary>
public sealed record TunnelStats(long PacketsToTunnel, long PacketsToAdapter, long BytesToTunnel, long BytesToAdapter);

/// <summary>
/// Owns one VPN session: brings up the Wintun adapter, runs the SSTP state
/// machine, pumps packets between the adapter and the tunnel, and undoes all of
/// the network configuration when the session ends (including on failure).
/// </summary>
public sealed class VpnSession : IAsyncDisposable
{
    private static readonly string[] PrivatePrefixes =
    {
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
    };

    private readonly Settings _settings;
    private readonly Action<TunnelProgress> _report;
    private readonly Action<string> _log;
    private readonly List<InstalledRoute> _installedRoutes = new();
    private readonly CancellationTokenSource _stop = new();

    private WintunSession? _adapter;
    private SstpTunnel? _tunnel;
    private Task? _pumpTask;
    private Task? _receiveTask;
    private Task? _receiveV6Task;
    private Task? _watchdogTask;
    private Task? _disconnectTask;
    private TunnelResult _result = new();
    private volatile bool _stopRequested;

    private long _packetsToTunnel;
    private long _packetsToAdapter;
    private long _bytesToTunnel;
    private long _bytesToAdapter;
    private int _sendFailures;

    public VpnSession(Settings settings, Action<TunnelProgress> report, Action<string> log)
    {
        _settings = settings;
        _report = report;
        _log = log;
    }

    public TunnelState State { get; private set; } = TunnelState.Idle;

    public string LocalAddress => _result.LocalAddress;

    public string PeerAddress => _result.PeerAddress;

    public IReadOnlyList<string> DnsServers => _result.DnsServers;

    public TunnelStats Stats => new(
        Interlocked.Read(ref _packetsToTunnel),
        Interlocked.Read(ref _packetsToAdapter),
        Interlocked.Read(ref _bytesToTunnel),
        Interlocked.Read(ref _bytesToAdapter));

    /// <summary>Runs until the session ends, normally or with a failure.</summary>
    public async Task<TunnelResult> RunAsync(CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _stop.Token);
        var token = linked.Token;

        try
        {
            if (!WindowsNetwork.IsElevated)
            {
                return Fail("Administrator rights are required to create the tunnel adapter. " +
                            "Close this window and start the app with \"Run as administrator\".");
            }

            var adapterError = await OpenAdapterAsync(token).ConfigureAwait(false);
            if (adapterError is not null)
            {
                return Fail(adapterError);
            }

            // Resolve how the server is reached *before* the tunnel takes over routing.
            var serverRoute = WindowsNetwork.FindRouteToHost(_settings.Host);
            if (serverRoute is null && _settings.AddDefaultRoute)
            {
                _log("Warning: could not determine the route to the VPN server; traffic may loop.");
            }

            _tunnel = new SstpTunnel(_settings, progress => Report(progress.State, progress.Message));
            _result = await _tunnel.ConnectAsync(token).ConfigureAwait(false);

            if (!_tunnel.IsConnected)
            {
                if (State != TunnelState.Failed && !string.IsNullOrEmpty(_result.Message))
                {
                    Report(TunnelState.Failed, _result.Message);
                }

                return _result;
            }

            // Start pumping before touching the routing table: the server may send
            // traffic as soon as CALL_CONNECTED is acknowledged.
            _receiveTask = Task.Run(() => PumpTunnelToAdapterAsync(ipv6: false, linked.Token), CancellationToken.None);
            if (_settings.EnableIpv6)
            {
                _receiveV6Task = Task.Run(
                    () => PumpTunnelToAdapterAsync(ipv6: true, linked.Token),
                    CancellationToken.None);
            }

            _pumpTask = Task.Run(() => PumpAdapterToTunnelAsync(linked.Token), CancellationToken.None);

            await ApplyNetworkConfigurationAsync(_result, serverRoute).ConfigureAwait(false);

            _watchdogTask = Task.Run(() => WarnIfNoTrafficAsync(linked.Token), CancellationToken.None);

            Report(TunnelState.Connected,
                $"Connected to {_settings.Host} — local {_result.LocalAddress}, peer {_result.PeerAddress}" +
                (_result.DnsServers.Count > 0 ? ", DNS " + string.Join(", ", _result.DnsServers) : string.Empty));

            // The tunnel reports its own failures through the progress callback,
            // so all that is left is to wait for the user to stop us or for the
            // data plane to disappear underneath us (server CALL_DISCONNECT,
            // CALL_ABORT or a socket error). Without this the window would keep
            // claiming "Connected" over a dead tunnel and never reconnect.
            var tunnelEnded = await WaitForStopAsync(_tunnel.Disconnected, token).ConfigureAwait(false);

            if (tunnelEnded && !_stopRequested && !token.IsCancellationRequested)
            {
                _result.Message = DescribeUnexpectedEnd();
                if (State is not TunnelState.Failed)
                {
                    Report(TunnelState.Disconnected, _result.Message);
                }
            }
            else
            {
                _result.Message = "Disconnected";
            }

            return _result;
        }
        catch (OperationCanceledException)
        {
            _result.Message = "Disconnected";
            return _result;
        }
        catch (Exception exception)
        {
            return Fail(exception.Message);
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the user stops the session or the tunnel ends on its own,
    /// returning true when the tunnel ended first. It never throws: a cancelled
    /// wait is a normal disconnect.
    /// </summary>
    internal static async Task<bool> WaitForStopAsync(Task tunnelEnd, CancellationToken token)
    {
        if (tunnelEnd.IsCompleted)
        {
            return true;
        }

        var stopSignal = Task.Delay(Timeout.Infinite, token);
        if (await Task.WhenAny(tunnelEnd, stopSignal).ConfigureAwait(false) == tunnelEnd)
        {
            return true;
        }

        try
        {
            // Observe the cancelled delay so the runtime does not report an
            // unobserved task exception.
            await stopSignal.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user asked to stop.
        }

        return false;
    }

    /// <summary>Why the tunnel stopped when nobody asked it to.</summary>
    private string DescribeUnexpectedEnd()
    {
        var message = _tunnel?.Result.Message;
        return string.IsNullOrWhiteSpace(message) || message == "Connected"
            ? "The server closed the connection."
            : message;
    }

    private TunnelResult Fail(string message)
    {
        _result.Message = message;
        Report(TunnelState.Failed, message);
        return _result;
    }

    private async Task<string?> OpenAdapterAsync(CancellationToken token)
    {
        Report(TunnelState.Connecting, $"Creating adapter \"{_settings.AdapterName}\"…");

        _adapter = new WintunSession();
        var error = _adapter.Open(_settings.AdapterName);
        if (error is not null)
        {
            return error;
        }

        Report(TunnelState.Connecting, "Waiting for Windows to register the adapter…");
        if (!await WindowsNetwork.WaitForAdapterAsync(_settings.AdapterName, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
        {
            return "Windows did not register the tunnel adapter in time.";
        }

        await Task.Delay(300, token).ConfigureAwait(false);
        return null;
    }

    private async Task ApplyNetworkConfigurationAsync(TunnelResult result, ServerRoute? serverRoute)
    {
        // Without an interface index nothing can be applied. Returning quietly
        // here used to leave the window claiming "Connected" with an adapter that
        // had no address, no DNS servers and no routes, so this is a hard failure.
        var interfaceIndex = await WindowsNetwork
            .WaitForInterfaceIndexAsync(_settings.AdapterName, TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        if (interfaceIndex == 0)
        {
            throw new InvalidOperationException(
                $"The tunnel adapter \"{_settings.AdapterName}\" was created, but Windows never gave it an IPv4 " +
                "interface index, so the address, DNS servers and routes could not be applied. " +
                "Start the app as administrator and check that the Wintun driver installed correctly.");
        }

        WindowsNetwork.EnableInterface(_settings.AdapterName);
        if (!WindowsNetwork.SetInterfaceMetric(_settings.AdapterName, 1))
        {
            _log("Warning: could not lower the tunnel interface metric: " + WindowsNetwork.LastError);
        }

        if (!string.IsNullOrEmpty(result.LocalAddress) && result.LocalAddress != "0.0.0.0")
        {
            if (!WindowsNetwork.SetInterfaceAddress(_settings.AdapterName, result.LocalAddress))
            {
                _log("Warning: could not assign the address to the adapter with netsh: " +
                     WindowsNetwork.LastError);
            }
            else if (!await WindowsNetwork.WaitForInterfaceAddressAsync(
                         _settings.AdapterName, result.LocalAddress, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            {
                // netsh returns before the IP Helper has finished, and the routes
                // below are far more likely to be rejected while the adapter has
                // no address of its own.
                _log("Warning: Windows has not applied " + result.LocalAddress +
                     " to the adapter yet; the routes below may fail.");
            }
        }
        else
        {
            _log("Warning: the server did not assign an IPv4 address (IPCP).");
        }

        if (!WindowsNetwork.SetMtu(_settings.AdapterName, _settings.Mtu))
        {
            _log("Warning: could not set the adapter MTU.");
        }

        if (_settings.EnableIpv6)
        {
            WindowsNetwork.ConfigureIpv6(_settings.AdapterName, "fe80::8000:8000");
        }

        if (result.DnsServers.Count > 0)
        {
            if (WindowsNetwork.SetDnsServers(_settings.AdapterName, result.DnsServers))
            {
                _log("DNS servers applied: " + string.Join(", ", result.DnsServers));
            }
            else
            {
                _log("Warning: could not apply the DNS servers.");
            }
        }

        if (serverRoute is not null)
        {
            // Pin the VPN server to the physical adapter. This /32 is more specific
            // than the /1 routes below, so the encrypted socket never loops back
            // into the tunnel it is carrying.
            AddRoute(serverRoute.InterfaceIndex, $"{serverRoute.ServerAddress}/32", serverRoute.NextHop, "original gateway");
        }

        if (_settings.RoutePrivateNetworks)
        {
            foreach (var prefix in PrivatePrefixes)
            {
                AddRoute(interfaceIndex, prefix, "0.0.0.0", "tunnel");
            }
        }

        foreach (var dns in _settings.CustomDnsServers)
        {
            // Keep DNS queries to the custom server inside the tunnel.
            AddRoute(interfaceIndex, dns + "/32", "0.0.0.0", "custom DNS");
        }

        if (_settings.AddCustomRoutes)
        {
            foreach (var prefix in _settings.CustomRoutes.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                AddRoute(interfaceIndex, prefix.Trim(), "0.0.0.0", "tunnel");
            }
        }

        if (_settings.AddDefaultRoute)
        {
            // Two /1 routes instead of 0.0.0.0/0: the physical default route stays
            // intact, removal is unambiguous, and Windows keeps the adapter's
            // on-link behaviour for the point-to-point peer.
            AddRoute(interfaceIndex, "0.0.0.0/1", "0.0.0.0", "tunnel");
            AddRoute(interfaceIndex, "128.0.0.0/1", "0.0.0.0", "tunnel");

            // Windows updates the routing table asynchronously, so give it a
            // moment before concluding that another interface won.
            if (!await WindowsNetwork.WaitForBestInterfaceAsync("1.1.1.1", interfaceIndex, TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false))
            {
                var best = WindowsNetwork.GetBestInterfaceIndex("1.1.1.1");
                throw new InvalidOperationException(
                    "The VPN connected, but Windows is still sending internet traffic outside the tunnel. " +
                    "The route table could not be updated (expected interface " + interfaceIndex +
                    ", Windows chose " + (best == 0 ? "unknown" : best.ToString()) + "). " +
                    "Run the app as administrator and check the route entries in the log.");
            }

            _log("Windows now routes internet traffic through the tunnel (interface " + interfaceIndex + ").");
        }
        else
        {
            _log("\"Route all internet traffic through the VPN\" is off: only the prefixes listed above " +
                 "are sent through the tunnel, everything else keeps using your normal connection.");
        }
    }

    private void AddRoute(uint interfaceIndex, string prefix, string nextHop, string what)
    {
        if (WindowsNetwork.AddRoute(interfaceIndex, prefix, nextHop))
        {
            _installedRoutes.Add(new InstalledRoute(interfaceIndex, prefix));
            _log($"Route {prefix} via {what} (if {interfaceIndex}).");
        }
        else
        {
            _log($"Warning: failed to add route {prefix} via {what} (if {interfaceIndex}): " +
                 WindowsNetwork.LastError);
        }
    }

    private async Task PumpTunnelToAdapterAsync(bool ipv6, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tunnel is not null && _adapter is { IsOpen: true })
        {
            var packet = ipv6
                ? await _tunnel.ReadIpv6PacketAsync(token).ConfigureAwait(false)
                : await _tunnel.ReadIpPacketAsync(token).ConfigureAwait(false);

            if (packet is null || packet.Length == 0)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            Interlocked.Increment(ref _packetsToAdapter);
            Interlocked.Add(ref _bytesToAdapter, packet.Length);

            if (!_adapter.Send(packet) && Interlocked.Increment(ref _sendFailures) == 1)
            {
                _log("Warning: the tunnel adapter could not accept a packet (send ring busy).");
            }
        }
    }

    private async Task PumpAdapterToTunnelAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _adapter is { IsOpen: true } && _tunnel is not null)
        {
            byte[]? packet;
            try
            {
                // The Wintun read blocks on the driver's read event, so this loop
                // stays idle instead of spinning.
                packet = _adapter.Receive(200, token);
            }
            catch (Exception exception)
            {
                _log("Adapter read failed: " + exception.Message);
                return;
            }

            if (packet is null || packet.Length == 0)
            {
                continue;
            }

            try
            {
                await _tunnel.SendIpPacketAsync(packet, token).ConfigureAwait(false);
                Interlocked.Increment(ref _packetsToTunnel);
                Interlocked.Add(ref _bytesToTunnel, packet.Length);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _log("Tunnel write failed: " + exception.Message);
                return;
            }
        }
    }

    private async Task WarnIfNoTrafficAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Stats.BytesToAdapter == 0 && Stats.BytesToTunnel == 0)
        {
            _log("Warning: no traffic has passed through the tunnel yet. " +
                 "If pages do not load, check the route table and DNS entries above.");
        }
    }

    public void Stop()
    {
        if (State is TunnelState.Disconnecting or TunnelState.Disconnected or TunnelState.Failed)
        {
            return;
        }

        Report(TunnelState.Disconnecting, "Disconnecting…");
        _stopRequested = true;

        // CALL_DISCONNECT is written from the thread pool because the caller is
        // usually the UI thread and must not block on the socket. Teardown waits
        // for this task, so the tunnel is not closed under the write.
        if (_tunnel is { } tunnel)
        {
            _disconnectTask = Task.Run(tunnel.Disconnect);
        }

        _stop.Cancel();
    }

    private async Task TeardownAsync()
    {
        try
        {
            // Stop the pumps first: they must not be writing to the tunnel while
            // the routes and the adapter go away.
            _stop.Cancel();

            WindowsNetwork.RestoreDns();

            foreach (var route in _installedRoutes)
            {
                WindowsNetwork.DeleteRoute(route.InterfaceIndex, route.Prefix);
            }

            _installedRoutes.Clear();

            if (_disconnectTask is { } disconnectTask)
            {
                _disconnectTask = null;
                await Task.WhenAny(disconnectTask, Task.Delay(3000)).ConfigureAwait(false);
            }

            if (_tunnel is not null)
            {
                await _tunnel.CloseAsync().ConfigureAwait(false);
            }

            foreach (var task in new[] { _pumpTask, _receiveTask, _receiveV6Task, _watchdogTask })
            {
                if (task is not null)
                {
                    await Task.WhenAny(task, Task.Delay(1500)).ConfigureAwait(false);
                }
            }

            if (_tunnel is not null)
            {
                await _tunnel.DisposeAsync().ConfigureAwait(false);
                _tunnel = null;
            }

            _adapter?.Dispose();
            _adapter = null;

            if (State is not (TunnelState.Failed or TunnelState.Disconnected))
            {
                Report(TunnelState.Disconnected, "Disconnected");
            }
        }
        catch (Exception exception)
        {
            _log("Teardown error: " + exception.Message);
        }
    }

    private void Report(TunnelState state, string message)
    {
        State = state;
        _report(new TunnelProgress(state, message));
        _log($"[{state}] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await TeardownAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
