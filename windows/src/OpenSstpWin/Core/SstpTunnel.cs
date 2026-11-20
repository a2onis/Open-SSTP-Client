using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using OpenSstpWin.Crypto;
using OpenSstpWin.Protocol;

namespace OpenSstpWin.Core;

public enum TunnelState
{
    Idle,
    Connecting,
    Authenticating,
    Negotiating,
    Connected,
    Disconnecting,
    Disconnected,
    Failed,
}

public sealed record TunnelProgress(TunnelState State, string Message);

public sealed class TunnelResult
{
    public string LocalAddress { get; set; } = string.Empty;
    public string PeerAddress { get; set; } = string.Empty;
    public List<string> DnsServers { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public byte[] ServerCertificate { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// The SSTP client state machine of the Android app on Windows:
/// TLS 1.2 → SSTP_DUPLEX_POST → CALL_CONNECT_REQUEST/ACK → LCP → PPP
/// authentication (PAP / MS-CHAPv2 / EAP-MS-CHAPv2) → CALL_CONNECTED →
/// IPCP / IPv6CP → data.
///
/// A single reader loop owns the TLS stream. Negotiation code consumes the
/// <c>control</c> queue, the steady-state pump consumes IP packets, so there is
/// exactly one reader of the socket at any time.
/// </summary>
public sealed class SstpTunnel : IAsyncDisposable
{
    private const string HttpDelimiter = "\r\n";

    private static readonly TimeSpan PppRequestInterval = TimeSpan.FromSeconds(3);
    private const int PppRequestCount = 10;

    private readonly Settings _settings;
    private readonly Action<TunnelProgress> _report;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly Channel<SstpControlPacket> _control = Channel.CreateUnbounded<SstpControlPacket>();
    private readonly Channel<SstpPppFrame> _ppp = Channel.CreateUnbounded<SstpPppFrame>();
    private readonly Channel<byte[]> _ipv4 = Channel.CreateBounded<byte[]>(512);
    private readonly Channel<byte[]> _ipv6 = Channel.CreateBounded<byte[]>(128);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Socket? _socket;
    private NetworkStream? _rawStream;
    private SslStream? _tls;
    private SstpPacketizer? _packetizer;
    private Task? _reader;
    private readonly byte[] _readBuffer = new byte[65536];

    private byte _frameId;
    private int _mru;
    private string _authProtocol = string.Empty;
    private byte[] _hlak = Array.Empty<byte>();
    private byte _hashProtocol = CertHashProtocolSha256;
    private readonly byte[] _nonce = new byte[32];
    private byte[] _peerCertificate = Array.Empty<byte>();

    private readonly byte[] _localIp = new byte[4];
    private readonly byte[] _peerIp = new byte[4];
    private readonly byte[] _dnsPrimary = new byte[4];
    private readonly byte[] _dnsSecondary = new byte[4];
    private readonly byte[] _ipv6Identifier = new byte[8];

    public SstpTunnel(Settings settings, Action<TunnelProgress> report)
    {
        _settings = settings;
        _report = report;
        _mru = settings.Mru;
    }

    public TunnelState State { get; private set; } = TunnelState.Idle;

    public TunnelResult Result { get; } = new();

    public int Mtu => _settings.Mtu;

    public bool IsConnected => State == TunnelState.Connected;

    // ------------------------------------------------------------ lifecycle

    public async Task<TunnelResult> ConnectAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linked.Token;

        try
        {
            SetState(TunnelState.Connecting, $"Connecting to {_settings.Host}:{_settings.Port}…");
            await OpenTlsAsync(token).ConfigureAwait(false);

            SetState(TunnelState.Connecting, "Sending SSTP_DUPLEX_POST…");
            await SendHttpRequestAsync(token).ConfigureAwait(false);

            _packetizer = new SstpPacketizer(ReadNextRecordAsync);
            _reader = Task.Run(() => ReaderLoopAsync(token), CancellationToken.None);

            SetState(TunnelState.Connecting, "Waiting for CALL_CONNECT_ACK…");
            await SendConnectRequestAsync(token).ConfigureAwait(false);

            SetState(TunnelState.Authenticating, "Negotiating LCP…");
            await RunLcpAsync(token).ConfigureAwait(false);

            SetState(TunnelState.Authenticating, $"Authenticating ({_authProtocol})…");
            await RunAuthenticationAsync(token).ConfigureAwait(false);

            await SendCallConnectedAsync(token).ConfigureAwait(false);

            if (_settings.EnableIpv4)
            {
                SetState(TunnelState.Negotiating, "Negotiating IPv4 (IPCP)…");
                await RunIpcpAsync(token).ConfigureAwait(false);
            }

            if (_settings.EnableIpv6)
            {
                SetState(TunnelState.Negotiating, "Negotiating IPv6 (IPv6CP)…");
                await RunIpv6cpAsync(token).ConfigureAwait(false);
            }

            if (!IsAllZero(_localIp))
            {
                Result.LocalAddress = new IPAddress(_localIp).ToString();
            }

            if (!IsAllZero(_peerIp))
            {
                Result.PeerAddress = new IPAddress(_peerIp).ToString();
            }

            if (_settings.RequestDns)
            {
                if (!IsAllZero(_dnsPrimary))
                {
                    Result.DnsServers.Add(new IPAddress(_dnsPrimary).ToString());
                }

                if (!IsAllZero(_dnsSecondary))
                {
                    Result.DnsServers.Add(new IPAddress(_dnsSecondary).ToString());
                }
            }

            SetState(TunnelState.Connected, "Connected");
            Result.Message = "Connected";

            // Return control to the session while the reader loop keeps running;
            // _disconnected completes on user disconnect, server disconnect or error.
            await WaitForEndAsync(token).ConfigureAwait(false);
            Result.Message = "Disconnected";
            State = TunnelState.Disconnected;
            return Result;
        }
        catch (OperationCanceledException)
        {
            Result.Message = "Disconnected";
            return Result;
        }
        catch (Exception exception)
        {
            Result.Message = Describe(exception);
            SetState(TunnelState.Failed, Result.Message);
            return Result;
        }
        finally
        {
            // The owning session calls CloseAsync(); nothing is torn down here so
            // the socket stays alive for an orderly CALL_DISCONNECT.
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        AuthenticationException => "TLS handshake failed: " + exception.Message,
        SocketException socket => $"Cannot reach the server ({socket.SocketErrorCode}). Check the host, port and network.",
        SstpProtocolException => exception.Message,
        IOException io when io.InnerException is AuthenticationException auth => "TLS handshake failed: " + auth.Message,
        IOException => "The connection to the server was lost.",
        TimeoutException => "The server stopped responding.",
        _ => exception.Message,
    };

    private void SetState(TunnelState state, string message)
    {
        State = state;
        _report(new TunnelProgress(state, message));
    }

    private async Task WaitForEndAsync(CancellationToken token)
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

    /// <summary>Completes when the tunnel stops for any reason.</summary>
    public Task Disconnected => _disconnected.Task;

    public void Disconnect()
    {
        _disconnected.TrySetResult();
        if (State is TunnelState.Connecting or TunnelState.Authenticating or TunnelState.Negotiating)
        {
            _cts.Cancel();
            return;
        }

        if (State != TunnelState.Connected)
        {
            _cts.Cancel();
            return;
        }

        try
        {
            SendRawAsync(SstpBuilder.CallDisconnect(), CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The socket may already be gone; cancelling is enough.
        }

        _cts.Cancel();
    }

    // ------------------------------------------------------------------ TLS

    private async Task OpenTlsAsync(CancellationToken token)
    {
        var host = _settings.Host.Trim();
        IPAddress? address = IPAddress.TryParse(host, out var parsed) ? parsed : null;

        if (address is not null && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            _socket = new Socket(System.Net.Sockets.AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        }
        else
        {
            _socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                if (address is not null)
                {
                    await _socket.ConnectAsync(new IPEndPoint(address, _settings.Port), timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    await _socket.ConnectAsync(host, _settings.Port, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out connecting to {host}:{_settings.Port}.");
            }
        }

        _rawStream = new NetworkStream(_socket, ownsSocket: true);

        if (_settings.UseProxy && !string.IsNullOrWhiteSpace(_settings.ProxyHost))
        {
            await EstablishProxyAsync(token).ConfigureAwait(false);
        }

        _tls = new SslStream(_rawStream, leaveInnerStreamOpen: false, ValidateCertificate);

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };

        await _tls.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);

        if (_tls.RemoteCertificate is not null)
        {
            _peerCertificate = _tls.RemoteCertificate.GetRawCertData();
            Result.ServerCertificate = _peerCertificate;
        }
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_settings.VerifyServerCertificate)
        {
            return errors == SslPolicyErrors.None;
        }

        // "Do not verify" accepts any chain. The SSTP crypto binding still proves
        // the server's identity because it hashes the very certificate we see.
        return true;
    }

    private async Task EstablishProxyAsync(CancellationToken token)
    {
        var request = new StringBuilder();
        request.Append($"CONNECT {_settings.Host}:{_settings.Port} HTTP/1.1{HttpDelimiter}");
        request.Append($"Host: {_settings.Host}:{_settings.Port}{HttpDelimiter}");
        request.Append($"SSTPVERSION: 1.0{HttpDelimiter}");

        if (!string.IsNullOrEmpty(_settings.ProxyUsername) || !string.IsNullOrEmpty(_settings.ProxyPassword))
        {
            var raw = Encoding.ASCII.GetBytes($"{_settings.ProxyUsername}:{_settings.ProxyPassword}");
            request.Append($"Proxy-Authorization: Basic {Convert.ToBase64String(raw)}{HttpDelimiter}");
        }

        request.Append(HttpDelimiter);
        await _rawStream!.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), token).ConfigureAwait(false);
        await _rawStream.FlushAsync(token).ConfigureAwait(false);

        var response = await ReadHttpHeadersAsync(_rawStream, token).ConfigureAwait(false);
        if (response.Contains("403"))
        {
            throw new SstpProtocolException("The HTTP proxy rejected the credentials.");
        }

        if (!response.Contains("200"))
        {
            throw new SstpProtocolException("The HTTP proxy refused the CONNECT request.");
        }
    }

    private async Task SendHttpRequestAsync(CancellationToken token)
    {
        var request = string.Concat(
            "SSTP_DUPLEX_POST /sra_{BA195980-CD49-458b-9E23-C84EE0ADCD75}/ HTTP/1.1", HttpDelimiter,
            "Content-Length: 18446744073709551615", HttpDelimiter,
            $"Host: {_settings.Host}", HttpDelimiter,
            $"SSTPCORRELATIONID: {{{Guid.NewGuid()}}}", HttpDelimiter,
            HttpDelimiter);

        await _tls!.WriteAsync(Encoding.ASCII.GetBytes(request), token).ConfigureAwait(false);
        await _tls.FlushAsync(token).ConfigureAwait(false);

        var response = await ReadHttpHeadersAsync(_tls, token).ConfigureAwait(false);
        if (!response.Contains("200"))
        {
            throw new SstpProtocolException("The server did not accept the SSTP_DUPLEX_POST request " +
                                            "(is this an SSTP server?).");
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken token)
    {
        var builder = new StringBuilder(512);
        var one = new byte[1];
        while (builder.Length < 16384)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)one[0]);
            if (builder.Length >= 4 && builder[^1] == '\n' && builder[^2] == '\r' && builder[^3] == '\n' && builder[^4] == '\r')
            {
                break;
            }
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- input

    /// <summary>Reads one complete SSTP packet worth of bytes from TLS.</summary>
    private async Task<byte[]> ReadNextRecordAsync(CancellationToken token)
    {
        var read = await _tls!.ReadAsync(_readBuffer, token).ConfigureAwait(false);
        if (read == 0)
        {
            throw new IOException("The server closed the connection.");
        }

        return _readBuffer.AsSpan(0, read).ToArray();
    }

    private async Task ReaderLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await _packetizer!.NextAsync(token).ConfigureAwait(false);
                var type = (ushort)((packet[0] << 8) | packet[1]);

                if (type == SstpMessage.PacketTypeControl)
                {
                    var control = SstpControlPacket.Parse(packet);
                    switch (control.Type)
                    {
                        case SstpMessage.EchoRequest:
                            await SendRawAsync(SstpBuilder.EchoResponse(), token).ConfigureAwait(false);
                            break;

                        case SstpMessage.CallDisconnect:
                        case SstpMessage.CallAbort:
                            _control.Writer.TryWrite(control);
                            _cts.Cancel();
                            _disconnected.TrySetResult();
                            return;

                        default:
                            _control.Writer.TryWrite(control);
                            break;
                    }

                    continue;
                }

                if (type != SstpMessage.PacketTypeData || packet.Length < 9)
                {
                    continue;
                }

                var protocol = (ushort)((packet[6] << 8) | packet[7]);
                var payload = packet.AsSpan(8).ToArray();

                if (protocol == Ppp.ProtocolIp)
                {
                    _ipv4.Writer.TryWrite(payload);
                }
                else if (protocol == Ppp.ProtocolIpv6)
                {
                    _ipv6.Writer.TryWrite(payload);
                }
                else
                {
                    _ppp.Writer.TryWrite(new SstpPppFrame(protocol, payload));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            if (!_cts.IsCancellationRequested)
            {
                Result.Message = Describe(exception);
                SetState(TunnelState.Failed, Result.Message);
            }

            _cts.Cancel();
            _control.Writer.TryComplete();
            _ppp.Writer.TryComplete();
            _ipv4.Writer.TryComplete();
            _ipv6.Writer.TryComplete();
            _disconnected.TrySetResult();
        }
    }

    // -------------------------------------------------------------- sending

    private async Task SendRawAsync(byte[] packet, CancellationToken token)
    {
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _tls!.WriteAsync(packet, token).ConfigureAwait(false);
            await _tls.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task SendPppAsync(ushort protocol, byte[] frame, CancellationToken token) =>
        SendRawAsync(SstpBuilder.Data(protocol, frame), token);

    /// <summary>Sends an IP packet from the TUN adapter into the tunnel.</summary>
    public async Task SendIpPacketAsync(byte[] packet, CancellationToken token)
    {
        if (packet.Length == 0 || !IsConnected)
        {
            return;
        }

        var version = packet[0] >> 4;
        var protocol = version switch
        {
            4 => Ppp.ProtocolIp,
            6 => Ppp.ProtocolIpv6,
            _ => (ushort)0,
        };

        if (protocol == 0)
        {
            return;
        }

        await SendRawAsync(SstpBuilder.Data(protocol, packet), token).ConfigureAwait(false);
    }

    /// <summary>Waits for the next inbound IPv4 packet.</summary>
    public async Task<byte[]?> ReadIpPacketAsync(CancellationToken token)
    {
        try
        {
            if (_ipv4.Reader.TryRead(out var packet))
            {
                return packet;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            return await _ipv4.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ SSTP setup

    private async Task SendConnectRequestAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendRawAsync(SstpBuilder.CallConnectRequest(), token).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            while (true)
            {
                SstpControlPacket packet;
                try
                {
                    packet = await ReadControlAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("No CALL_CONNECT_ACK from the server.");
                }

                switch (packet.Type)
                {
                    case SstpMessage.CallConnectAck:
                    {
                        var request = packet.Find(SstpAttributeId.CryptoBindingRequest)
                            ?? throw new SstpProtocolException("CALL_CONNECT_ACK without a crypto binding request.");
                        var reader = new ByteReader(request.Value);
                        reader.Skip(3);
                        var bitmask = reader.U8();
                        _hashProtocol = bitmask switch
                        {
                            1 => CertHashProtocolSha1,
                            2 or 3 => CertHashProtocolSha256,
                            _ => throw new SstpProtocolException($"Unsupported certificate hash bitmask 0x{bitmask:X2}."),
                        };
                        reader.ByteArray(32).CopyTo(_nonce, 0);
                        return;
                    }

                    case SstpMessage.CallConnectNak:
                        throw new SstpProtocolException("The server rejected the SSTP call (CALL_CONNECT_NAK).");

                    case SstpMessage.CallDisconnect:
                        throw new SstpProtocolException("The server disconnected during setup.");

                    case SstpMessage.CallAbort:
                        throw new SstpProtocolException("The server aborted the SSTP call.");

                    default:
                        continue;
                }
            }
        }
    }

    private async Task<SstpControlPacket> ReadControlAsync(CancellationToken token)
    {
        try
        {
            return await _control.Reader.ReadAsync(token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("The server closed the connection.");
        }
    }

    private async Task SendCallConnectedAsync(CancellationToken token)
    {
        if (_hlak.Length == 0)
        {
            throw new SstpProtocolException("Authentication did not produce a key for the crypto binding.");
        }

        var sha256 = _hashProtocol == CertHashProtocolSha256;
        var certHash = SstpCrypto.CertHash(_peerCertificate, sha256);
        var cmk = SstpCrypto.DeriveCmk(_hlak, sha256);

        var packet = SstpBuilder.CallConnectedForMac(_hashProtocol, _nonce, certHash);
        var compoundMac = SstpCrypto.CompoundMac(cmk, packet, sha256);
        var final = SstpBuilder.CallConnectedWithMac(_hashProtocol, _nonce, certHash, compoundMac);
        await SendRawAsync(final, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- LCP/PPP

    private byte NextFrameId()
    {
        _frameId = unchecked((byte)(_frameId + 1));
        return _frameId;
    }

    private async Task RunLcpAsync(CancellationToken token)
    {
        var requestCount = PppRequestCount;
        var mruRejected = false;

        while (true)
        {
            var id = NextFrameId();
            var options = mruRejected
                ? Array.Empty<byte>()
                : PppOption.WriteList(new[] { PppOption.MakeUInt16(Ppp.OptionLcpMru, (ushort)_mru) });
            await SendPppAsync(Ppp.ProtocolLcp, Ppp.ControlFrame(Ppp.CodeConfigureRequest, id, options), token).ConfigureAwait(false);

            var clientReady = false;
            var serverReady = false;
            var restart = false;

            while (!clientReady || !serverReady)
            {
                requestCount--;
                if (requestCount < 0)
                {
                    throw new SstpProtocolException("LCP negotiation timed out: the server did not answer.");
                }

                (byte Code, byte Id, byte[] Payload) frame;
                try
                {
                    frame = await ReadFrameAsync(Ppp.ProtocolLcp, PppRequestInterval, token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    restart = true;
                    break;
                }

                var received = PppOption.ParseList(frame.Payload);

                switch (frame.Code)
                {
                    case Ppp.CodeConfigureRequest:
                    {
                        serverReady = false;

                        var unknown = received.Where(o => o.Type is not (Ppp.OptionLcpMru or Ppp.OptionLcpAuth)).ToList();
                        if (unknown.Count > 0)
                        {
                            await SendPppAsync(Ppp.ProtocolLcp,
                                Ppp.ControlFrame(Ppp.CodeConfigureReject, frame.Id, PppOption.WriteList(unknown)), token).ConfigureAwait(false);
                            continue;
                        }

                        var nak = new List<PppOption>();
                        var mru = received.FirstOrDefault(o => o.Type == Ppp.OptionLcpMru);
                        if (mru is not null && PppOption.ReadUInt16(mru) < _settings.Mtu)
                        {
                            nak.Add(PppOption.MakeUInt16(Ppp.OptionLcpMru, (ushort)_settings.Mtu));
                        }

                        var authOption = received.FirstOrDefault(o => o.Type == Ppp.OptionLcpAuth);
                        var chosen = ChooseAuth(authOption);
                        if (chosen is null)
                        {
                            nak.Add(PreferredAuthOption());
                        }
                        else
                        {
                            _authProtocol = chosen;
                        }

                        if (nak.Count > 0)
                        {
                            await SendPppAsync(Ppp.ProtocolLcp,
                                Ppp.ControlFrame(Ppp.CodeConfigureNak, frame.Id, PppOption.WriteList(nak)), token).ConfigureAwait(false);
                            continue;
                        }

                        await SendPppAsync(Ppp.ProtocolLcp,
                            Ppp.ControlFrame(Ppp.CodeConfigureAck, frame.Id, frame.Payload), token).ConfigureAwait(false);
                        serverReady = true;
                        break;
                    }

                    case Ppp.CodeConfigureAck when frame.Id == id:
                        clientReady = true;
                        break;

                    case Ppp.CodeConfigureNak when frame.Id == id:
                    case Ppp.CodeConfigureReject when frame.Id == id:
                    {
                        var mruReply = received.FirstOrDefault(o => o.Type == Ppp.OptionLcpMru);
                        if (mruReply is not null)
                        {
                            if (frame.Code == Ppp.CodeConfigureNak)
                            {
                                _mru = Math.Clamp(PppOption.ReadUInt16(mruReply), 576, _settings.Mru);
                            }
                            else
                            {
                                mruRejected = true;
                            }
                        }

                        var authReply = received.FirstOrDefault(o => o.Type == Ppp.OptionLcpAuth);
                        if (authReply is not null && frame.Code == Ppp.CodeConfigureReject)
                        {
                            var next = _settings.AuthProtocols.FirstOrDefault(p => !string.Equals(p, _authProtocol, StringComparison.OrdinalIgnoreCase));
                            if (next is not null)
                            {
                                _authProtocol = next;
                            }
                        }

                        restart = true;
                        break;
                    }
                }

                if (restart)
                {
                    break;
                }
            }

            if (restart)
            {
                continue;
            }

            if (string.IsNullOrEmpty(_authProtocol))
            {
                _authProtocol = Auth.Pap;
            }

            return;
        }
    }

    private string? ChooseAuth(PppOption? authOption)
    {
        if (authOption is null || authOption.Value.Length < 2)
        {
            return null;
        }

        var reader = new ByteReader(authOption.Value);
        var protocol = reader.U16();
        var holder = reader.Remaining > 0 ? reader.ByteArray(reader.Remaining) : Array.Empty<byte>();

        return protocol switch
        {
            Ppp.ProtocolEap => Enabled(Auth.EapMsChapV2) ? Auth.EapMsChapV2 : null,
            Ppp.ProtocolChap => holder.Length == 1 && holder[0] == Ppp.ChapAlgorithmMsChapV2 && Enabled(Auth.MsChapV2)
                ? Auth.MsChapV2
                : null,
            Ppp.ProtocolPap => Enabled(Auth.Pap) ? Auth.Pap : null,
            _ => null,
        };
    }

    private bool Enabled(string protocol) =>
        _settings.AuthProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase);

    private PppOption PreferredAuthOption()
    {
        var writer = new ByteWriter();
        switch (_settings.AuthProtocols[0])
        {
            case Auth.EapMsChapV2:
                writer.U16(Ppp.ProtocolEap);
                break;
            case Auth.MsChapV2:
                writer.U16(Ppp.ProtocolChap);
                writer.U8(Ppp.ChapAlgorithmMsChapV2);
                break;
            default:
                writer.U16(Ppp.ProtocolPap);
                break;
        }

        return PppOption.MakeBytes(Ppp.OptionLcpAuth, writer.ToArray());
    }

    /// <summary>
    /// Reads the next PPP control frame for <paramref name="protocol"/>.
    /// Must only be called while no other component is reading the stream:
    /// during negotiation the reader loop feeds PPP frames through the
    /// <c>_ppp</c> queue.
    /// </summary>
    private async Task<(byte Code, byte Id, byte[] Payload)> ReadFrameAsync(ushort protocol, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        deadline.CancelAfter(timeout);

        while (true)
        {
            SstpPppFrame frame;
            try
            {
                frame = await _ppp.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            if (frame.Protocol == protocol)
            {
                var (code, id, payload) = Ppp.ParseControlFrame(frame.Payload);
                return (code, id, payload);
            }

            if (frame.Protocol == Ppp.ProtocolLcp)
            {
                // Server keep-alive while another protocol is being negotiated.
                await AnswerLcpAsync(frame.Payload, token).ConfigureAwait(false);
            }
        }
    }

    private async Task AnswerLcpAsync(byte[] payload, CancellationToken token)
    {
        var (code, id, body) = Ppp.ParseControlFrame(payload);
        switch (code)
        {
            case Ppp.CodeEchoRequest:
            {
                var reply = new byte[payload.Length];
                payload.CopyTo(reply, 0);
                reply[0] = Ppp.CodeEchoReply;
                await SendRawAsync(SstpBuilder.Data(Ppp.ProtocolLcp, reply), token).ConfigureAwait(false);
                break;
            }

            case Ppp.CodeTerminateRequest:
            {
                var ack = Ppp.ControlFrame(Ppp.CodeTerminateAck, id, body);
                await SendRawAsync(SstpBuilder.Data(Ppp.ProtocolLcp, ack), token).ConfigureAwait(false);
                _cts.Cancel();
                break;
            }
        }
    }

    // -------------------------------------------------------- authentication

    private async Task RunAuthenticationAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.AuthTimeoutSeconds));
        var authToken = deadline.Token;

        switch (_authProtocol)
        {
            case Auth.Pap:
                await RunPapAsync(authToken).ConfigureAwait(false);
                _hlak = new byte[32];
                break;

            case Auth.MsChapV2:
                await RunChapAsync(authToken).ConfigureAwait(false);
                break;

            case Auth.EapMsChapV2:
                await RunEapAsync(authToken).ConfigureAwait(false);
                break;

            default:
                throw new SstpProtocolException("No supported PPP authentication protocol was negotiated.");
        }
    }

    private async Task RunPapAsync(CancellationToken token)
    {
        var id = NextFrameId();
        var user = Encoding.ASCII.GetBytes(_settings.Username);
        var password = Encoding.ASCII.GetBytes(_settings.Password);

        var payload = new ByteWriter();
        payload.U8((byte)user.Length);
        payload.Bytes(user);
        payload.U8((byte)password.Length);
        payload.Bytes(password);

        await SendPppAsync(Ppp.ProtocolPap, Ppp.ControlFrame(Ppp.CodeConfigureRequest, id, payload.ToArray()), token).ConfigureAwait(false);

        while (true)
        {
            var (code, frameId, body) = await ReadFrameAsync(Ppp.ProtocolPap, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (frameId != id)
            {
                continue;
            }

            if (code == Ppp.CodeConfigureAck)
            {
                return;
            }

            if (code == Ppp.CodeConfigureNak)
            {
                throw new SstpProtocolException("Authentication failed (PAP)" + ReadMessage(body) + ".");
            }
        }
    }

    private static string ReadMessage(byte[] body)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        var length = Math.Min(body[0], body.Length - 1);
        return length == 0 ? string.Empty : ": " + Encoding.ASCII.GetString(body, 1, length).Trim().TrimStart('=');
    }

    private async Task RunChapAsync(CancellationToken token)
    {
        var auth = new MsChapV2(_settings.Username, _settings.Password);

        while (true)
        {
            var (code, id, body) = await ReadFrameAsync(Ppp.ProtocolChap, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

            if (code == Ppp.ChapChallenge)
            {
                var reader = new ByteReader(body);
                var value = reader.ByteArray(reader.U8());
                var response = auth.ProcessChallenge(value);

                var payload = new ByteWriter();
                payload.U8((byte)response.Length);
                payload.Bytes(response);
                payload.Bytes(Encoding.ASCII.GetBytes(_settings.Username));

                await SendPppAsync(Ppp.ProtocolChap, Ppp.ControlFrame(Ppp.ChapResponse, id, payload.ToArray()), token).ConfigureAwait(false);
                continue;
            }

            if (code == Ppp.ChapSuccess)
            {
                if (!auth.VerifyAuthenticator(body))
                {
                    throw new SstpProtocolException("The server's MS-CHAPv2 authenticator response did not verify.");
                }

                auth.PrepareHlak();
                _hlak = auth.Hlak;
                return;
            }

            if (code == Ppp.ChapFailure)
            {
                throw new SstpProtocolException("Authentication failed (MS-CHAPv2)" + ReadMessage(body) + ".");
            }
        }
    }

    private async Task RunEapAsync(CancellationToken token)
    {
        var auth = new MsChapV2(_settings.Username, _settings.Password);
        var resultExpected = false;

        while (true)
        {
            var (code, id, body) = await ReadFrameAsync(Ppp.ProtocolEap, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

            if (code == Ppp.EapFailure)
            {
                throw new SstpProtocolException("Authentication failed (EAP-MS-CHAPv2).");
            }

            if (code == Ppp.EapSuccess)
            {
                if (resultExpected)
                {
                    return;
                }

                continue;
            }

            if (code != Ppp.EapRequest)
            {
                continue;
            }

            var reader = new ByteReader(body);
            var type = reader.U8();
            var typeData = reader.ByteArray(reader.Remaining);

            if (type == Ppp.EapTypeIdentity)
            {
                var identity = new ByteWriter();
                identity.U8(Ppp.EapTypeIdentity);
                identity.Bytes(Encoding.ASCII.GetBytes(_settings.Username));
                await SendPppAsync(Ppp.ProtocolEap, Ppp.ControlFrame(Ppp.EapResponse, id, identity.ToArray()), token).ConfigureAwait(false);
                continue;
            }

            if (type == Ppp.EapTypeNotification)
            {
                continue;
            }

            if (type != Ppp.EapTypeMsAuth)
            {
                var nak = new ByteWriter();
                nak.U8(Ppp.EapTypeNak);
                nak.U8(Ppp.EapTypeMsAuth);
                await SendPppAsync(Ppp.ProtocolEap, Ppp.ControlFrame(Ppp.EapResponse, id, nak.ToArray()), token).ConfigureAwait(false);
                continue;
            }

            var inner = new ByteReader(typeData);
            var chapCode = inner.U8();
            var chapId = inner.U8();
            var chapLength = inner.U16();
            var chapPayload = inner.ByteArray(Math.Max(0, chapLength - 4));

            if (chapCode == Ppp.ChapChallenge)
            {
                resultExpected = false;
                var challengeReader = new ByteReader(chapPayload);
                var value = challengeReader.ByteArray(challengeReader.U8());
                var response = auth.ProcessChallenge(value);

                var responsePayload = new ByteWriter();
                responsePayload.U8((byte)response.Length);
                responsePayload.Bytes(response);
                responsePayload.Bytes(Encoding.ASCII.GetBytes(_settings.Username));

                var innerFrame = Ppp.ControlFrame(Ppp.ChapResponse, chapId, responsePayload.ToArray());
                var outer = new ByteWriter();
                outer.U8(Ppp.EapTypeMsAuth);
                outer.Bytes(innerFrame);
                await SendPppAsync(Ppp.ProtocolEap, Ppp.ControlFrame(Ppp.EapResponse, id, outer.ToArray()), token).ConfigureAwait(false);
                continue;
            }

            if (chapCode == Ppp.ChapSuccess)
            {
                if (!auth.VerifyAuthenticator(chapPayload))
                {
                    throw new SstpProtocolException("The server's MS-CHAPv2 authenticator response did not verify.");
                }

                resultExpected = true;
                auth.PrepareHlak();
                _hlak = auth.Hlak;

                var outer = new ByteWriter();
                outer.U8(Ppp.EapTypeMsAuth);
                outer.U8(Ppp.ChapSuccess);
                outer.U8(chapId);
                outer.U16(4);
                await SendPppAsync(Ppp.ProtocolEap, Ppp.ControlFrame(Ppp.EapResponse, id, outer.ToArray()), token).ConfigureAwait(false);
                continue;
            }

            if (chapCode == Ppp.ChapFailure)
            {
                throw new SstpProtocolException("Authentication failed (EAP-MS-CHAPv2).");
            }
        }
    }

    // ------------------------------------------------------------------ NCP

    private async Task RunIpcpAsync(CancellationToken token)
    {
        var id = NextFrameId();
        await SendIpcpRequestAsync(id, token).ConfigureAwait(false);

        var clientReady = false;
        var serverReady = false;

        while (!clientReady || !serverReady)
        {
            (byte Code, byte Id, byte[] Payload) frame;
            try
            {
                frame = await ReadFrameAsync(Ppp.ProtocolIpcp, PppRequestInterval, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                id = NextFrameId();
                await SendIpcpRequestAsync(id, token).ConfigureAwait(false);
                continue;
            }

            var received = PppOption.ParseList(frame.Payload);

            switch (frame.Code)
            {
                case Ppp.CodeConfigureRequest:
                {
                    var unknown = received.Where(o => o.Type is not (Ppp.OptionIpcpIp or Ppp.OptionIpcpDns)).ToList();
                    if (unknown.Count > 0)
                    {
                        await SendPppAsync(Ppp.ProtocolIpcp,
                            Ppp.ControlFrame(Ppp.CodeConfigureReject, frame.Id, PppOption.WriteList(unknown)), token).ConfigureAwait(false);
                        continue;
                    }

                    var peerIp = received.FirstOrDefault(o => o.Type == Ppp.OptionIpcpIp);
                    if (peerIp is not null && peerIp.Value.Length >= 4 && !IsAllZero(peerIp.Value))
                    {
                        peerIp.Value.AsSpan(0, 4).CopyTo(_peerIp);
                    }

                    await SendPppAsync(Ppp.ProtocolIpcp,
                        Ppp.ControlFrame(Ppp.CodeConfigureAck, frame.Id, frame.Payload), token).ConfigureAwait(false);
                    serverReady = true;
                    break;
                }

                case Ppp.CodeConfigureAck when frame.Id == id:
                    clientReady = true;
                    break;

                case Ppp.CodeConfigureNak when frame.Id == id:
                {
                    var ip = received.FirstOrDefault(o => o.Type == Ppp.OptionIpcpIp);
                    if (ip is not null && ip.Value.Length >= 4)
                    {
                        ip.Value.AsSpan(0, 4).CopyTo(_localIp);
                    }

                    var dns = received.FirstOrDefault(o => o.Type == Ppp.OptionIpcpDns);
                    if (dns is not null)
                    {
                        var addresses = SplitDns(dns.Value);
                        if (addresses.Count > 0)
                        {
                            addresses[0].CopyTo(_dnsPrimary, 0);
                        }

                        if (addresses.Count > 1)
                        {
                            addresses[1].CopyTo(_dnsSecondary, 0);
                        }
                    }

                    if (IsAllZero(_localIp))
                    {
                        throw new SstpProtocolException("The server did not offer an IPv4 address.");
                    }

                    id = NextFrameId();
                    await SendIpcpRequestAsync(id, token).ConfigureAwait(false);
                    break;
                }

                case Ppp.CodeConfigureReject when frame.Id == id:
                {
                    var rejected = received.Select(o => o.Type).ToList();
                    if (rejected.Contains(Ppp.OptionIpcpIp))
                    {
                        throw new SstpProtocolException("The server rejected the IP address option (IPCP).");
                    }

                    id = NextFrameId();
                    await SendIpcpRequestAsync(id, token).ConfigureAwait(false);
                    break;
                }
            }
        }

        if (IsAllZero(_peerIp))
        {
            // Some servers only advertise their address in the ACK they send.
            _peerIp[0] = _localIp[0];
            _peerIp[1] = _localIp[1];
            _peerIp[2] = _localIp[2];
            _peerIp[3] = (byte)(_localIp[3] == 1 ? 2 : _localIp[3] - 1);
        }
    }

    private async Task SendIpcpRequestAsync(byte id, CancellationToken token)
    {
        var options = new List<PppOption> { PppOption.MakeBytes(Ppp.OptionIpcpIp, _localIp) };
        if (_settings.RequestDns)
        {
            options.Add(PppOption.MakeBytes(Ppp.OptionIpcpDns, _dnsPrimary));
        }

        await SendPppAsync(Ppp.ProtocolIpcp, Ppp.ControlFrame(Ppp.CodeConfigureRequest, id, PppOption.WriteList(options)), token).ConfigureAwait(false);
    }

    private static List<byte[]> SplitDns(byte[] value)
    {
        var result = new List<byte[]>();
        for (var offset = 0; offset + 4 <= value.Length && result.Count < 2; offset += 4)
        {
            result.Add(value.AsSpan(offset, 4).ToArray());
        }

        return result;
    }

    private static bool IsAllZero(byte[] value) => value.All(b => b == 0);

    private async Task RunIpv6cpAsync(CancellationToken token)
    {
        Random.Shared.NextBytes(_ipv6Identifier);
        _ipv6Identifier[0] &= 0xFD;

        var id = NextFrameId();
        await SendIpv6cpRequestAsync(id, token).ConfigureAwait(false);

        var clientReady = false;
        var serverReady = false;

        while (!clientReady || !serverReady)
        {
            (byte Code, byte Id, byte[] Payload) frame;
            try
            {
                frame = await ReadFrameAsync(Ppp.ProtocolIpv6cp, PppRequestInterval, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                id = NextFrameId();
                await SendIpv6cpRequestAsync(id, token).ConfigureAwait(false);
                continue;
            }

            switch (frame.Code)
            {
                case Ppp.CodeConfigureRequest:
                    await SendPppAsync(Ppp.ProtocolIpv6cp,
                        Ppp.ControlFrame(Ppp.CodeConfigureAck, frame.Id, frame.Payload), token).ConfigureAwait(false);
                    serverReady = true;
                    break;

                case Ppp.CodeConfigureAck when frame.Id == id:
                    clientReady = true;
                    break;

                case Ppp.CodeConfigureNak when frame.Id == id:
                {
                    var reply = PppOption.ParseList(frame.Payload)
                        .FirstOrDefault(o => o.Type == Ppp.OptionIpv6cpIdentifier);
                    if (reply is not null && reply.Value.Length >= 8)
                    {
                        reply.Value.AsSpan(0, 8).CopyTo(_ipv6Identifier);
                    }

                    id = NextFrameId();
                    await SendIpv6cpRequestAsync(id, token).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    private Task SendIpv6cpRequestAsync(byte id, CancellationToken token)
    {
        var options = new[] { PppOption.MakeBytes(Ppp.OptionIpv6cpIdentifier, _ipv6Identifier) };
        return SendPppAsync(Ppp.ProtocolIpv6cp, Ppp.ControlFrame(Ppp.CodeConfigureRequest, id, PppOption.WriteList(options)), token);
    }

    // ------------------------------------------------------------- shutdown

    public async Task CloseAsync()
    {
        try
        {
            _control.Writer.TryComplete();
            _ppp.Writer.TryComplete();
            _ipv4.Writer.TryComplete();
            _ipv6.Writer.TryComplete();

            if (_reader is not null)
            {
                await Task.WhenAny(_reader, Task.Delay(1500)).ConfigureAwait(false);
            }

            _packetizer?.Dispose();

            if (_tls is not null)
            {
                try
                {
                    await _tls.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore teardown errors.
                }
            }

            _rawStream?.Dispose();
            _socket?.Dispose();
        }
        catch
        {
            // Ignore teardown errors.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await CloseAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>A PPP frame received inside an SSTP data packet.</summary>
internal readonly record struct SstpPppFrame(ushort Protocol, byte[] Payload);
