using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSstpWin;

/// <summary>PPP authentication methods the client can negotiate.</summary>
public static class Auth
{
    public const string Pap = "PAP";
    public const string MsChapV2 = "MSCHAPv2";
    public const string EapMsChapV2 = "EAP-MSCHAPv2";

    public static IEnumerable<string> All => new[] { Pap, MsChapV2, EapMsChapV2 };

    public static bool IsKnown(string value) =>
        All.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>TLS protocol versions exposed the way the Android client exposes them.</summary>
public static class TlsModes
{
    public const string Default = "DEFAULT";
    public const string Tls12 = "TLS12";
    public const string Tls13 = "TLS13";

    public static IReadOnlyList<string> All { get; } = new[] { Default, Tls12, Tls13 };

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>Window colour themes (the Android app calls this "Theme").</summary>
public static class AppThemes
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static IReadOnlyList<string> All { get; } = new[] { System, Light, Dark };

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? System;
}

/// <summary>
/// Connection settings stored as JSON in
/// %LOCALAPPDATA%\OpenSstpWin\settings.json. Every value can also be supplied on
/// the command line, which does not write the file.
/// </summary>
public sealed class Settings
{
    public static IReadOnlyList<string> DefaultSourceUrls { get; } = new[]
    {
        "https://raw.githubusercontent.com/F0rc3Run/F0rc3Run/refs/heads/main/sstp-configs/sstp_with_country.txt",
        "https://raw.githubusercontent.com/Delta-Kronecker/Vpn-Gate/refs/heads/main/sstp_hosts.txt",
    };

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Enabled PPP authentication methods, in preference order.</summary>
    public List<string> AuthProtocols { get; set; } = new() { Auth.Pap, Auth.MsChapV2 };

    public bool VerifyServerCertificate { get; set; } = true;

    /// <summary>Trust only the certificates in <see cref="CaBundlePath"/> (Android SSL_DO_SPECIFY_CERT).</summary>
    public bool UseCustomCa { get; set; }

    /// <summary>
    /// Optional PEM file (or folder) with the trusted root certificates used
    /// when <see cref="UseCustomCa"/> is enabled.
    /// </summary>
    public string CaBundlePath { get; set; } = string.Empty;

    /// <summary>DEFAULT lets Windows negotiate; TLS12/TLS13 force one version.</summary>
    public string TlsVersion { get; set; } = TlsModes.Default;

    /// <summary>Send a different TLS server name (SNI) than the endpoint host.</summary>
    public bool UseCustomSni { get; set; }
    public string CustomSniHostname { get; set; } = string.Empty;

    /// <summary>
    /// Best-effort cipher-suite restriction. Windows Schannel ignores the policy,
    /// so the connection continues with the OS defaults in that case.
    /// </summary>
    public bool UseCipherSuites { get; set; }
    public List<string> CipherSuites { get; set; } = new();

    public int Mru { get; set; } = 1500;
    public int Mtu { get; set; } = 1500;
    public int AuthTimeoutSeconds { get; set; } = 15;

    public bool EnableIpv4 { get; set; } = true;

    /// <summary>Ask the server for a specific IPv4 address (PPPL_DO_REQUEST_STATIC).</summary>
    public bool RequestStaticIpv4 { get; set; }
    public string StaticIpv4Address { get; set; } = string.Empty;

    public bool EnableIpv6 { get; set; }
    public bool RequestDns { get; set; } = true;

    /// <summary>Use this DNS server (IPv4) instead of the one offered by the server.</summary>
    public bool UseCustomDns { get; set; }
    public string CustomDnsAddress { get; set; } = string.Empty;

    /// <summary>Route 0.0.0.0/1 and 128.0.0.0/1 through the tunnel.</summary>
    public bool AddDefaultRoute { get; set; } = true;

    /// <summary>Route the RFC1918 ranges so LAN traffic also uses the tunnel.</summary>
    public bool RoutePrivateNetworks { get; set; } = true;

    /// <summary>Add the prefixes in <see cref="CustomRoutes"/> to the tunnel.</summary>
    public bool AddCustomRoutes { get; set; } = true;

    /// <summary>Extra prefixes ("10.0.0.0/8" style) routed into the tunnel.</summary>
    public List<string> CustomRoutes { get; set; } = new();

    public bool UseProxy { get; set; }
    public string ProxyHost { get; set; } = string.Empty;
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;

    /// <summary>Retry a dropped or failed connection automatically.</summary>
    public bool EnableReconnection { get; set; }
    public int ReconnectionCount { get; set; } = 3;
    public int ReconnectionIntervalSeconds { get; set; } = 10;

    public string AdapterName { get; set; } = "OpenSSTP";
    public bool SaveLog { get; set; } = true;

    /// <summary>Empty means %LOCALAPPDATA%\OpenSstpWin\logs.</summary>
    public string LogFolder { get; set; } = string.Empty;

    public string AppTheme { get; set; } = AppThemes.System;

    /// <summary>HTTPS text lists used by "Find fastest from sources".</summary>
    public List<string> SourceUrls { get; set; } = DefaultSourceUrls.ToList();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenSstpWin");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static string ProfilesPath => Path.Combine(Directory, "profiles.json");

    [JsonIgnore]
    public string LogDirectory => string.IsNullOrWhiteSpace(LogFolder)
        ? Path.Combine(Directory, "logs")
        : LogFolder.Trim();

    [JsonIgnore]
    public IReadOnlyList<string> CustomDnsServers => ParseIpv4Addresses(CustomDnsAddress);

    [JsonIgnore]
    public bool HasStaticIpv4 => RequestStaticIpv4 && TryParseIpv4(StaticIpv4Address, out _);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt settings file falls back to defaults instead of blocking startup.
        }

        return new Settings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static Settings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions)
                       ?? throw new InvalidOperationException("The profile file does not contain settings.");
        settings.Normalize();
        return settings;
    }

    public Settings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
    }

    /// <summary>Replaces every writable value on this instance with a deep copy of another.</summary>
    public void CopyFrom(Settings other)
    {
        var source = other.Clone();
        foreach (var property in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(this, property.GetValue(source));
            }
        }
    }

    /// <summary>Clamps user input into ranges the protocol and Windows accept.</summary>
    public void Normalize()
    {
        Port = Port is > 0 and <= 65535 ? Port : 443;
        ProxyPort = ProxyPort is > 0 and <= 65535 ? ProxyPort : 8080;
        Mtu = Math.Clamp(Mtu, 576, 2000);
        Mru = Math.Clamp(Mru, 576, 2000);
        AuthTimeoutSeconds = Math.Clamp(AuthTimeoutSeconds, 3, 300);
        AdapterName = string.IsNullOrWhiteSpace(AdapterName) ? "OpenSSTP" : AdapterName.Trim();
        TlsVersion = TlsModes.Normalize(TlsVersion);
        AppTheme = AppThemes.Normalize(AppTheme);
        ReconnectionCount = Math.Clamp(ReconnectionCount, 0, 50);
        ReconnectionIntervalSeconds = Math.Clamp(ReconnectionIntervalSeconds, 1, 3600);
        Host = Host?.Trim() ?? string.Empty;
        Username ??= string.Empty;
        Password ??= string.Empty;
        ProxyHost = ProxyHost?.Trim() ?? string.Empty;
        ProxyUsername ??= string.Empty;
        ProxyPassword ??= string.Empty;
        CustomSniHostname = CustomSniHostname?.Trim() ?? string.Empty;
        StaticIpv4Address = StaticIpv4Address?.Trim() ?? string.Empty;
        CustomDnsAddress = CustomDnsAddress?.Trim() ?? string.Empty;
        CaBundlePath = CaBundlePath?.Trim() ?? string.Empty;
        LogFolder = LogFolder?.Trim() ?? string.Empty;

        // Settings written by the 1.10.x Windows build only had caBundlePath.
        // Keep trusting that bundle after the upgrade.
        if (!UseCustomCa && CaBundlePath.Length > 0)
        {
            UseCustomCa = true;
        }

        AuthProtocols = (AuthProtocols ?? new List<string>())
            .Where(Auth.IsKnown)
            .Select(p => Auth.All.First(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        if (AuthProtocols.Count == 0)
        {
            AuthProtocols = new List<string> { Auth.Pap, Auth.MsChapV2 };
        }

        if (!EnableIpv4 && !EnableIpv6)
        {
            EnableIpv4 = true;
        }

        CustomRoutes = (CustomRoutes ?? new List<string>())
            .Select(route => route?.Trim() ?? string.Empty)
            .Where(route => route.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        CipherSuites = (CipherSuites ?? new List<string>())
            .Select(suite => suite?.Trim() ?? string.Empty)
            .Where(suite => suite.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        SourceUrls = (SourceUrls ?? new List<string>())
            .Select(url => url?.Trim() ?? string.Empty)
            .Where(OpenSstpWin.Core.SourceFinder.IsValidSourceUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    /// <summary>Returns user-facing errors; an empty list means the settings can be used.</summary>
    public IReadOnlyList<string> Validate(bool requireEndpoint = true)
    {
        var errors = new List<string>();

        if (requireEndpoint)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                errors.Add("The server hostname or IP address is required.");
            }
            else if (!OpenSstpWin.Core.EndpointParser.IsValidHost(Host))
            {
                errors.Add("The server address is not a valid hostname or IP address.");
            }

            if (Port is < 1 or > 65535)
            {
                errors.Add("The server port must be between 1 and 65535.");
            }
        }

        if (!EnableIpv4 && !EnableIpv6)
        {
            errors.Add("Enable IPv4, IPv6, or both.");
        }

        if (RequestStaticIpv4 && !TryParseIpv4(StaticIpv4Address, out _))
        {
            errors.Add("A valid static IPv4 address is required when it is requested.");
        }

        if (UseCustomDns && CustomDnsServers.Count == 0)
        {
            errors.Add("Enter at least one valid IPv4 DNS server address or turn off the custom DNS server.");
        }

        if (UseProxy)
        {
            if (string.IsNullOrWhiteSpace(ProxyHost))
            {
                errors.Add("A proxy hostname is required when the proxy is enabled.");
            }

            if (ProxyPort is < 1 or > 65535)
            {
                errors.Add("The proxy port must be between 1 and 65535.");
            }
        }

        if (UseCustomSni && string.IsNullOrWhiteSpace(CustomSniHostname))
        {
            errors.Add("Enter the custom SNI hostname or turn the option off.");
        }

        if (UseCipherSuites && CipherSuites.Count == 0)
        {
            errors.Add("Select at least one cipher suite or turn the option off.");
        }

        if (UseCustomCa)
        {
            if (string.IsNullOrWhiteSpace(CaBundlePath))
            {
                errors.Add("Choose the custom CA bundle file or folder.");
            }
            else if (!File.Exists(CaBundlePath) && !System.IO.Directory.Exists(CaBundlePath))
            {
                errors.Add("The custom CA bundle file or folder does not exist: " + CaBundlePath);
            }
        }

        if (AuthProtocols.Count == 0)
        {
            errors.Add("Select at least one PPP authentication protocol.");
        }

        if (EnableReconnection && ReconnectionCount < 1)
        {
            errors.Add("Reconnection count must be at least 1.");
        }

        if (ReconnectionIntervalSeconds < 1)
        {
            errors.Add("The reconnection interval must be at least 1 second.");
        }

        return errors;
    }

    public static bool TryParseIpv4(string? value, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IPAddress.TryParse(value.Trim(), out var parsed) &&
            parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address = parsed.ToString();
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ParseIpv4Addresses(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var part in value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseIpv4(part, out var address) && !result.Contains(address))
            {
                result.Add(address);
            }
        }

        return result;
    }
}
