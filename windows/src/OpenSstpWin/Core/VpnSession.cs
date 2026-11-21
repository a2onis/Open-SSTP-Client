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
    private TunnelResult _result = new();

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

            await WaitUntilStoppedAsync(linked.Token).ConfigureAwait(false);
            _result.Message = "Disconnected";
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

    private async Task WaitUntilStoppedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect.
        }
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
        var interfaceIndex = WindowsNetwork.GetInterfaceIndex(_settings.AdapterName);
        if (interfaceIndex == 0)
        {
            _log("Warning: the tunnel adapter index is unknown; routes were not installed.");
            return;
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

            var best = WindowsNetwork.GetBestInterfaceIndex("1.1.1.1");
            if (best != interfaceIndex)
            {
                throw new InvalidOperationException(
                    "The VPN connected, but Windows is still sending internet traffic outside the tunnel. " +
                    "The route table could not be updated (expected interface " + interfaceIndex +
                    ", Windows chose " + (best == 0 ? "unknown" : best.ToString()) + "). " +
                    "Run the app as administrator and check the route entries in the log.");
            }

            _log("Windows now routes internet traffic through the tunnel (interface " + interfaceIndex + ").");
        }

        await Task.CompletedTask.ConfigureAwait(false);
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
        _tunnel?.Disconnect();
        _stop.Cancel();
    }

    private async Task TeardownAsync()
    {
        try
        {
            WindowsNetwork.RestoreDns();

            foreach (var route in _installedRoutes)
            {
                WindowsNetwork.DeleteRoute(route.InterfaceIndex, route.Prefix);
            }

            _installedRoutes.Clear();

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
