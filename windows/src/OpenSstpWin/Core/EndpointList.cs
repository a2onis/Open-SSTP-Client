using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OpenSstpWin.Core;

/// <summary>An SSTP endpoint, not a network range. Missing ports use the SSTP default.</summary>
public sealed record Endpoint(string Host, int Port)
{
    public string DisplayName => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// Parses the one-endpoint-per-line lists used by the Android client: a
/// hostname, IP address, host:port, bracketed IPv6 address with port, or an
/// HTTP(S) URL. Optional "country |" labels and "#" comments are ignored.
/// </summary>
public static class EndpointParser
{
    private static readonly Regex HostLabel =
        new("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static Endpoint? ParseLine(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var line = raw;
        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            line = line[..hash];
        }

        var pipe = line.LastIndexOf('|');
        if (pipe >= 0)
        {
            line = line[(pipe + 1)..];
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        var hasScheme = line.Contains("://", StringComparison.Ordinal);
        if (!hasScheme && line.Contains('/'))
        {
            // Never treat a CIDR as a single host.
            return null;
        }

        if (!hasScheme && line.Count(c => c == ':') > 1 && !line.StartsWith('['))
        {
            // Bare IPv6 has no port.
            line = "[" + line + "]";
        }

        if (!Uri.TryCreate(hasScheme ? line : "https://" + line, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (uri.Authority.EndsWith(':'))
        {
            return null;
        }

        var host = NormalizeHost(uri.Host);
        if (host.Length == 0 || host.Contains('%'))
        {
            return null;
        }

        if (!host.Contains(':'))
        {
            if (!IsValidHostName(host))
            {
                return null;
            }
        }
        else if (!IPAddress.TryParse(host, out var address) ||
                 address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        // Match the Android client: an unspecified port always means 443,
        // regardless of whether the line used an http or https URL.
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        if (port is < 1 or > 65535)
        {
            return null;
        }

        return new Endpoint(host, port);
    }

    /// <summary>
    /// Parses at most <paramref name="max"/> unique endpoints. When the text
    /// contains more, <paramref name="exceeded"/> is set and parsing stops.
    /// </summary>
    public static List<Endpoint> ParseText(string? text, int max, out bool exceeded)
    {
        exceeded = false;
        var result = new List<Endpoint>();
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return result;
        }

        var seen = new HashSet<Endpoint>();
        foreach (var raw in text.Split('\n'))
        {
            var endpoint = ParseLine(raw.TrimEnd('\r'));
            if (endpoint is null || !seen.Add(endpoint))
            {
                continue;
            }

            if (result.Count >= max)
            {
                exceeded = true;
                break;
            }

            result.Add(endpoint);
        }

        return result;
    }

    public static string NormalizeHost(string host) =>
        (host ?? string.Empty).Trim().Trim('[', ']');

    /// <summary>Accepts a DNS name, an IPv4 address or an IPv6 address (with or without brackets).</summary>
    public static bool IsValidHost(string? host)
    {
        var value = NormalizeHost(host ?? string.Empty);
        if (value.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        return IsValidHostName(value);
    }

    /// <summary>Validates a non-IPv6 hostname exactly like the Android parser.</summary>
    public static bool IsValidHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Contains('%'))
        {
            return false;
        }

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || !HostLabel.IsMatch(label))
            {
                return false;
            }
        }

        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            if (labels.Length != 4 || labels.Any(label => !byte.TryParse(label, out _)))
            {
                return false;
            }
        }

        return true;
    }
}
