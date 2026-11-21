using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace OpenSstpWin.Core;

/// <summary>An endpoint that accepted a TCP connection, with the measured connect time.</summary>
public sealed record TimedEndpoint(Endpoint Endpoint, long LatencyMs);

/// <summary>Progress reported while endpoints are being probed.</summary>
public sealed class FinderProgress
{
    public int Tested { get; init; }
    public int Total { get; init; }
    public int Reachable { get; init; }
}

/// <summary>The sorted result of a "Find fastest from sources" comparison.</summary>
public sealed class FinderOutcome
{
    public IReadOnlyList<TimedEndpoint> Results { get; init; } = Array.Empty<TimedEndpoint>();
    public IReadOnlyList<string> FailedSources { get; init; } = Array.Empty<string>();
    public int TotalEndpoints { get; init; }
}

/// <summary>
/// Downloads endpoint lists only when asked, deduplicates them, probes the
/// advertised port of every endpoint with bounded concurrency, and sorts the
/// reachable ones by TCP connect time. This is the Windows counterpart of the
/// Android "Find fastest from sources" screen.
/// </summary>
public sealed class SourceFinder
{
    public const int MaxEndpoints = 1000;
    public const int MaxDownloadBytes = 1024 * 1024;
    public const int MaxConcurrency = 12;
    public const int ConnectTimeoutMs = 3000;
    public const int DownloadTimeoutMs = 10000;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(DownloadTimeoutMs / 1000.0),
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSstpWin/1.11");
        return client;
    }

    /// <summary>Source lists must be credential-free HTTPS URLs.</summary>
    public static bool IsValidSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    public async Task<FinderOutcome> CompareAsync(
        IReadOnlyList<string> sources,
        IProgress<FinderProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("Add a source URL first.");
        }

        var endpoints = new List<Endpoint>();
        var seen = new HashSet<Endpoint>();
        var failedSources = new List<string>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidSourceUrl(source))
            {
                failedSources.Add(source);
                continue;
            }

            string text;
            try
            {
                text = await DownloadAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedSources.Add(source);
                continue;
            }

            foreach (var endpoint in EndpointParser.ParseText(text, MaxEndpoints + 1, out var exceeded))
            {
                if (exceeded)
                {
                    throw new InvalidOperationException($"More than {MaxEndpoints} endpoints in the sources.");
                }

                if (seen.Add(endpoint))
                {
                    endpoints.Add(endpoint);
                }
            }

            if (endpoints.Count > MaxEndpoints)
            {
                throw new InvalidOperationException($"More than {MaxEndpoints} endpoints in the sources.");
            }
        }

        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid endpoints found; {failedSources.Count} source(s) failed.");
        }

        var results = new List<TimedEndpoint>();
        var resultsLock = new object();
        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var completed = 0;
        var reachable = 0;

        var probes = endpoints.Select(async endpoint =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var latency = await ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (latency is long milliseconds)
                {
                    lock (resultsLock)
                    {
                        results.Add(new TimedEndpoint(endpoint, milliseconds));
                    }

                    Interlocked.Increment(ref reachable);
                }
            }
            finally
            {
                gate.Release();
            }

            var done = Interlocked.Increment(ref completed);
            if (done % 10 == 0 || done == endpoints.Count)
            {
                progress?.Report(new FinderProgress
                {
                    Tested = done,
                    Total = endpoints.Count,
                    Reachable = Volatile.Read(ref reachable),
                });
            }
        }).ToList();

        await Task.WhenAll(probes).ConfigureAwait(false);

        var sorted = results
            .OrderBy(result => result.LatencyMs)
            .ThenBy(result => result.Endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FinderOutcome
        {
            Results = sorted,
            FailedSources = failedSources,
            TotalEndpoints = endpoints.Count,
        };
    }

    private static async Task<string> DownloadAsync(string source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(DownloadTimeoutMs / 1000.0));

        using var response = await Http
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if ((int)response.StatusCode != 200)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException("Source exceeds 1 MiB");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<long?> ProbeAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeoutMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>One-off TCP reachability test used by the configuration list.</summary>
public static class EndpointProbe
{
    public const int DefaultTimeoutMs = 5000;

    public static async Task<string> TestAsync(
        string? host,
        int port,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        var clean = EndpointParser.NormalizeHost(host ?? string.Empty);
        if (clean.Length == 0 || port is < 1 or > 65535)
        {
            return "Invalid host or port";
        }

        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(clean, port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return $"TCP reachable · {stopwatch.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "TCP connection timed out";
        }
        catch (Exception exception)
        {
            return "TCP connection failed: " + exception.Message;
        }
    }
}
