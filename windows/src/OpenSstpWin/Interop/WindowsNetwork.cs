using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenSstpWin.Interop;

/// <summary>Where traffic for the VPN server itself must be sent.</summary>
public sealed record ServerRoute(uint InterfaceIndex, string NextHop, string ServerAddress);

/// <summary>
/// Applies the tunnel's IP configuration with netsh.exe and the IP Helper API —
/// both present on every Windows 10/11 installation. Routes are added to the
/// active store only, so nothing survives a reboot.
/// </summary>
public static class WindowsNetwork
{
    private static (string Adapter, string Servers, bool FromDhcp)? _dnsSnapshot;

    /// <summary>Last netsh failure, shown in the session log when routing breaks.</summary>
    public static string LastError { get; private set; } = string.Empty;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<bool> WaitForAdapterAsync(string adapterName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetInterfaceIndex(adapterName) != 0)
            {
                return true;
            }

            await Task.Delay(150).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Finds the IPv4 interface index of an adapter by its friendly name.</summary>
    public static uint GetInterfaceIndex(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
                !nic.Description.Contains(adapterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var index = (uint)nic.GetIPProperties().GetIPv4Properties().Index;
                if (index != 0)
                {
                    return index;
                }
            }
            catch
            {
                // The adapter has no IPv4 stack yet.
            }
        }

        return 0;
    }

    /// <summary>Assigns the negotiated address to the tunnel adapter (point to point).</summary>
    public static bool EnableInterface(string adapterName)
    {
        return Run("netsh", $"interface set interface name=\"{adapterName}\" admin=enable");
    }

    public static bool SetInterfaceAddress(string adapterName, string address)
    {
        return Run("netsh",
            $"interface ipv4 set address name=\"{adapterName}\" source=static address={address} mask=255.255.255.255 gateway=none");
    }

    /// <summary>
    /// Gives the tunnel preference over Wi-Fi/Ethernet. Routes are still more
    /// specific than 0.0.0.0/0, but a lower metric avoids Windows treating the
    /// Wintun adapter as a fallback for directly reachable prefixes.
    /// </summary>
    public static bool SetInterfaceMetric(string adapterName, int metric)
    {
        metric = Math.Clamp(metric, 1, 9999);
        return Run("netsh", $"interface ipv4 set interface interface=\"{adapterName}\" metric={metric}");
    }

    public static bool ConfigureIpv6(string adapterName, string address)
    {
        return Run("netsh", $"interface ipv6 add address \"{adapterName}\" {address} store=active");
    }

    public static bool SetMtu(string adapterName, int mtu)
    {
        mtu = Math.Clamp(mtu, 576, 2000);
        var ipv4 = Run("netsh",
            $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=active");
        Run("netsh", $"interface ipv6 set subinterface \"{adapterName}\" mtu={mtu} store=active");
        return ipv4;
    }

    // ------------------------------------------------------------------ DNS

    public static bool SetDnsServers(string adapterName, IEnumerable<string> servers)
    {
        var list = servers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (list.Count == 0)
        {
            return true;
        }

        if (_dnsSnapshot is null)
        {
            var current = GetDnsServers(adapterName);
            _dnsSnapshot = (adapterName, string.Join(",", current), current.Count == 0);
        }

        if (!Run("netsh",
                $"interface ipv4 set dnsservers name=\"{adapterName}\" source=static address={list[0]} register=primary validate=no"))
        {
            return false;
        }

        for (var i = 1; i < list.Count; i++)
        {
            Run("netsh",
                $"interface ipv4 add dnsservers name=\"{adapterName}\" address={list[i]} index={i + 1} validate=no");
        }

        return true;
    }

    public static List<string> GetDnsServers(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return nic.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        return new List<string>();
    }

    public static void RestoreDns()
    {
        if (_dnsSnapshot is not { } snapshot)
        {
            return;
        }

        _dnsSnapshot = null;

        try
        {
            if (snapshot.FromDhcp || string.IsNullOrWhiteSpace(snapshot.Servers))
            {
                Run("netsh", $"interface ipv4 set dnsservers name=\"{snapshot.Adapter}\" source=dhcp");
                return;
            }

            var servers = snapshot.Servers.Split(',', StringSplitOptions.RemoveEmptyEntries);
            Run("netsh",
                $"interface ipv4 set dnsservers name=\"{snapshot.Adapter}\" source=static address={servers[0]} register=primary validate=no");
            for (var i = 1; i < servers.Length; i++)
            {
                Run("netsh",
                    $"interface ipv4 add dnsservers name=\"{snapshot.Adapter}\" address={servers[i]} index={i + 1} validate=no");
            }
        }
        catch
        {
            // Restoring DNS is best effort and must never block shutdown.
        }
    }

    // --------------------------------------------------------------- routes

    /// <summary>
    /// Adds an IPv4 route (active store: removed automatically on reboot).
    /// On-link prefixes must omit nexthop entirely: netsh rejects an explicit
    /// nexthop of 0.0.0.0, which previously left the tunnel with no routes even
    /// though the tunnel itself was connected.
    /// </summary>
    public static bool AddRoute(uint interfaceIndex, string prefix, string nextHop)
    {
        return Run("netsh", BuildAddRouteArguments(interfaceIndex, prefix, nextHop));
    }

    internal static string BuildAddRouteArguments(uint interfaceIndex, string prefix, string nextHop)
    {
        var gateway = string.IsNullOrWhiteSpace(nextHop) ? "0.0.0.0" : nextHop.Trim();
        var onLink = gateway is "0.0.0.0" or "::";
        return onLink
            ? $"interface ipv4 add route prefix={prefix} interface={interfaceIndex} metric=1 store=active"
            : $"interface ipv4 add route prefix={prefix} interface={interfaceIndex} nexthop={gateway} metric=1 store=active";
    }

    public static bool DeleteRoute(uint interfaceIndex, string prefix)
    {
        return Run("netsh",
            $"interface ipv4 delete route prefix={prefix} interface={interfaceIndex} store=active");
    }

    /// <summary>
    /// Resolves how this machine currently reaches the VPN server, so a host
    /// route can pin the encrypted connection to the physical adapter.
    /// </summary>
    /// <summary>Returns the interface Windows would use for an IPv4 destination.</summary>
    public static uint GetBestInterfaceIndex(string destination)
    {
        if (!IPAddress.TryParse(destination, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        return GetBestInterface(IpToUInt32(address), out var bestInterface) == 0
            ? bestInterface
            : 0;
    }

    public static ServerRoute? FindRouteToHost(string host)
    {
        IPAddress? address;
        if (IPAddress.TryParse(host, out var parsed))
        {
            address = parsed;
        }
        else
        {
            try
            {
                address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch
            {
                return null;
            }
        }

        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6-only server: leave the routing table alone.
            return null;
        }

        var destination = IpToUInt32(address);
        if (GetBestInterface(destination, out var bestIndex) != 0 || bestIndex == 0)
        {
            return null;
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            int index;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties().Index;
            }
            catch
            {
                continue;
            }

            if ((uint)index != bestIndex)
            {
                continue;
            }

            var gateway = nic.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();

            return new ServerRoute(bestIndex, gateway ?? "0.0.0.0", address.ToString());
        }

        return null;
    }

    private static uint IpToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    // ------------------------------------------------------------ processes

    internal static bool Run(string executable, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore.
                }

                return false;
            }

            var output = stdout.Result;
            var error = stderr.Result.Trim();
            if (process.ExitCode == 0)
            {
                LastError = string.Empty;
                return true;
            }

            LastError = (error.Length > 0 ? error : output.Trim()) +
                        " (" + executable + " " + arguments + ")";
            return false;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destination, out uint bestInterface);
}
