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

/// <summary>
/// Connection settings stored as JSON in
/// %LOCALAPPDATA%\OpenSstpWin\settings.json. Every value can also be supplied on
/// the command line, which does not write the file.
/// </summary>
public sealed class Settings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Enabled PPP authentication methods, in preference order.</summary>
    public List<string> AuthProtocols { get; set; } = new() { Auth.Pap, Auth.MsChapV2 };

    public bool VerifyServerCertificate { get; set; } = true;

    /// <summary>Optional PEM file with extra trusted root certificates.</summary>
    public string CaBundlePath { get; set; } = string.Empty;

    public int Mru { get; set; } = 1500;
    public int Mtu { get; set; } = 1500;
    public int AuthTimeoutSeconds { get; set; } = 15;

    public bool EnableIpv4 { get; set; } = true;
    public bool EnableIpv6 { get; set; }
    public bool RequestDns { get; set; } = true;

    /// <summary>Route 0.0.0.0/1 and 128.0.0.0/1 through the tunnel.</summary>
    public bool AddDefaultRoute { get; set; } = true;

    /// <summary>Route the RFC1918 ranges so LAN traffic also uses the tunnel.</summary>
    public bool RoutePrivateNetworks { get; set; } = true;

    /// <summary>Extra prefixes ("10.0.0.0/8" style) routed into the tunnel.</summary>
    public List<string> CustomRoutes { get; set; } = new();

    public bool UseProxy { get; set; }
    public string ProxyHost { get; set; } = string.Empty;
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;

    public string AdapterName { get; set; } = "OpenSSTP";
    public bool SaveLog { get; set; } = true;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenSstpWin");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public string LogDirectory => Path.Combine(Directory, "logs");

    private static readonly JsonSerializerOptions JsonOptions = new()
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
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Settings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
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
        AuthProtocols = (AuthProtocols ?? new List<string>())
            .Where(Auth.IsKnown)
            .Select(p => Auth.All.First(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        if (AuthProtocols.Count == 0)
        {
            AuthProtocols = new List<string> { Auth.Pap, Auth.MsChapV2 };
        }
    }
}
