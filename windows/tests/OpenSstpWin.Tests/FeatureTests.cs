using System.IO;
using OpenSstpWin;
using OpenSstpWin.Core;
using OpenSstpWin.Interop;
using Xunit;

namespace OpenSstpWin.Tests;

/// <summary>
/// Locks down the features imported from the Android client: endpoint-list
/// parsing, settings normalization/validation, and the profile store.
/// </summary>
public class FeatureTests
{
    [Fact]
    public void OnLinkRoutesOmitTheNextHopThatNetshRejects()
    {
        var tunnel = WindowsNetwork.BuildAddRouteArguments(42, "0.0.0.0/1", "0.0.0.0");
        Assert.Contains("interface=42", tunnel);
        Assert.DoesNotContain("nexthop=", tunnel);

        var server = WindowsNetwork.BuildAddRouteArguments(9, "192.0.2.1/32", "192.0.2.254");
        Assert.Contains("nexthop=192.0.2.254", server);
    }

    [Theory]
    [InlineData("Vietnam | VPN.EXAMPLE.ORG:443", "vpn.example.org", 443)]
    [InlineData("192.0.2.1", "192.0.2.1", 443)]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
    [InlineData("2001:db8::1", "2001:db8::1", 443)]
    [InlineData("https://user:password@vpn.example.org:8443/path?x=1", "vpn.example.org", 8443)]
    [InlineData("  # comment 192.0.2.9", "", 0)]
    public void EndpointParsingMatchesTheAndroidClient(string line, string host, int port)
    {
        var endpoint = EndpointParser.ParseLine(line);
        if (host.Length == 0)
        {
            Assert.Null(endpoint);
            return;
        }

        Assert.NotNull(endpoint);
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("192.0.2.0/24")]
    [InlineData("2001:db8::/64")]
    [InlineData("999.1.1.1")]
    [InlineData("example.org:0")]
    [InlineData("example.org:65536")]
    [InlineData("example.org:")]
    [InlineData("ftp://example.org")]
    [InlineData("example.org:abc")]
    public void InvalidEndpointsAreRejected(string line) => Assert.Null(EndpointParser.ParseLine(line));

    [Fact]
    public void EndpointTextRespectsTheImportLimit()
    {
        var text = string.Join("\n", Enumerable.Range(0, 205).Select(index => "host" + index + ".example.org"));

        var endpoints = EndpointParser.ParseText(text, 200, out var exceeded);

        Assert.True(exceeded);
        Assert.Equal(200, endpoints.Count);
    }

    [Fact]
    public void EndpointDisplayNameBracketsIpv6()
    {
        Assert.Equal("vpn.example.org:443", new Endpoint("vpn.example.org", 443).DisplayName);
        Assert.Equal("[2001:db8::1]:8443", new Endpoint("2001:db8::1", 8443).DisplayName);
    }

    [Fact]
    public void SettingsNormalizeKeepsValuesInRange()
    {
        var settings = new Settings
        {
            Port = 0,
            Mtu = 10,
            Mru = 9000,
            AuthTimeoutSeconds = 0,
            TlsVersion = "ssl3",
            AppTheme = "purple",
            ReconnectionCount = -4,
            ReconnectionIntervalSeconds = 0,
            AuthProtocols = new List<string> { "bogus" },
        };

        settings.Normalize();

        Assert.Equal(443, settings.Port);
        Assert.Equal(576, settings.Mtu);
        Assert.Equal(2000, settings.Mru);
        Assert.Equal(3, settings.AuthTimeoutSeconds);
        Assert.Equal(TlsModes.Default, settings.TlsVersion);
        Assert.Equal(AppThemes.System, settings.AppTheme);
        Assert.Equal(0, settings.ReconnectionCount);
        Assert.Equal(1, settings.ReconnectionIntervalSeconds);
        Assert.Equal(2, settings.AuthProtocols.Count);
        Assert.Contains(Auth.Pap, settings.AuthProtocols);
        Assert.Contains(Auth.MsChapV2, settings.AuthProtocols);
    }

    [Fact]
    public void SettingsValidationSeparatesEndpointFromAdvancedOptions()
    {
        var settings = new Settings { UseCustomDns = true, CustomDnsAddress = "not-an-address" };

        var withEndpoint = settings.Validate();
        var advancedOnly = settings.Validate(requireEndpoint: false);

        Assert.Contains(withEndpoint, error => error.Contains("hostname", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advancedOnly, error => error.Contains("hostname", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(advancedOnly, error => error.Contains("DNS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaticAddressAndDnsAreValidated()
    {
        var settings = new Settings
        {
            Host = "vpn.example.org",
            RequestStaticIpv4 = true,
            StaticIpv4Address = "10.0.0.5",
            UseCustomDns = true,
            CustomDnsAddress = "1.1.1.1, 8.8.8.8",
        };

        Assert.Empty(settings.Validate());
        Assert.True(settings.HasStaticIpv4);
        Assert.Equal(2, settings.CustomDnsServers.Count);
        Assert.Equal("1.1.1.1", settings.CustomDnsServers[0]);
        Assert.Equal("8.8.8.8", settings.CustomDnsServers[1]);

        settings.StaticIpv4Address = "300.1.1.1";
        settings.CustomDnsAddress = "::1";
        Assert.Equal(2, settings.Validate().Count);
    }

    [Fact]
    public void CloneAndCopyFromDoNotShareMutableLists()
    {
        var source = new Settings
        {
            Host = "vpn.example.org",
            CustomRoutes = new List<string> { "10.0.0.0/8" },
            AuthProtocols = new List<string> { Auth.Pap },
        };

        var clone = source.Clone();
        clone.CustomRoutes.Add("192.168.0.0/16");
        clone.AuthProtocols.Add(Auth.MsChapV2);

        Assert.Single(source.CustomRoutes);
        Assert.Single(source.AuthProtocols);

        var target = new Settings();
        target.CopyFrom(source);
        source.CustomRoutes.Add("172.16.0.0/12");
        Assert.Single(target.CustomRoutes);
    }

    [Fact]
    public void SettingsJsonRoundTripKeepsEveryOption()
    {
        var settings = new Settings
        {
            Host = "vpn.example.org",
            Port = 8443,
            Username = "me",
            Password = "secret",
            UseCustomCa = true,
            CaBundlePath = "ca.pem",
            UseCustomSni = true,
            CustomSniHostname = "sni.example.org",
            UseCustomDns = true,
            CustomDnsAddress = "1.1.1.1",
            EnableIpv6 = true,
            RequestStaticIpv4 = true,
            StaticIpv4Address = "10.0.0.9",
            EnableReconnection = true,
            ReconnectionCount = 5,
            AppTheme = AppThemes.Dark,
            SourceUrls = new List<string> { "https://example.org/list.txt" },
        };

        var restored = Settings.FromJson(settings.ToJson());

        Assert.Equal(settings.Host, restored.Host);
        Assert.Equal(settings.Port, restored.Port);
        Assert.Equal(settings.Password, restored.Password);
        Assert.True(restored.UseCustomCa);
        Assert.Equal("ca.pem", restored.CaBundlePath);
        Assert.Equal("sni.example.org", restored.CustomSniHostname);
        Assert.Equal("1.1.1.1", restored.CustomDnsAddress);
        Assert.True(restored.EnableIpv6);
        Assert.Equal("10.0.0.9", restored.StaticIpv4Address);
        Assert.Equal(5, restored.ReconnectionCount);
        Assert.Equal(AppThemes.Dark, restored.AppTheme);
        Assert.Single(restored.SourceUrls);
    }

    [Fact]
    public void SourceUrlsMustBeHttpsWithoutCredentials()
    {
        Assert.True(SourceFinder.IsValidSourceUrl("https://example.org/list.txt"));
        Assert.False(SourceFinder.IsValidSourceUrl("http://example.org/list.txt"));
        Assert.False(SourceFinder.IsValidSourceUrl("https://user:pass@example.org/list.txt"));
        Assert.False(SourceFinder.IsValidSourceUrl(""));
        Assert.False(SourceFinder.IsValidSourceUrl("not a url"));
    }

    [Fact]
    public void ProfileStorePersistsRenamesAndDeletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opensstpwin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var store = new ProfileStore(path);

            Assert.True(store.AddOrUpdate("Work", new Settings
            {
                Host = "vpn.example.org",
                Port = 443,
                Username = "me",
            }));
            Assert.True(store.AddOrUpdate("Home", new Settings { Host = "192.0.2.1", Port = 8443 }));
            Assert.Null(store.LoadError);

            var reloaded = new ProfileStore(path);
            Assert.Equal(2, reloaded.Profiles.Count);
            Assert.Equal("vpn.example.org", reloaded.Find("work")!.Settings.Host);

            Assert.True(reloaded.AddOrUpdate("Office", new Settings { Host = "198.51.100.1" }, "Work"));
            Assert.Null(reloaded.Find("Work"));
            Assert.NotNull(reloaded.Find("Office"));

            Assert.True(reloaded.Delete("Home"));
            Assert.Single(new ProfileStore(path).Profiles);

            var clash = new ProfileStore(path);
            Assert.True(clash.AddOrUpdate("Office", new Settings { Host = "203.0.113.1" }));
            Assert.True(clash.AddOrUpdate("Personal", new Settings { Host = "203.0.113.3" }));
            Assert.False(clash.AddOrUpdate("Personal", new Settings { Host = "203.0.113.2" }, "Office"));
            Assert.Equal("203.0.113.1", new ProfileStore(path).Find("Office")!.Settings.Host);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
