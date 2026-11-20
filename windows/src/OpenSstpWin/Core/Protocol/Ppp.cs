namespace OpenSstpWin.Protocol;

/// <summary>PPP constants and frame helpers (RFC 1661/1662, RFC 1334, RFC 2759).</summary>
public static class Ppp
{
    public const ushort HdlcHeader = 0xFF03;

    public const ushort ProtocolLcp = 0xC021;
    public const ushort ProtocolPap = 0xC023;
    public const ushort ProtocolChap = 0xC223;
    public const ushort ProtocolEap = 0xC227;
    public const ushort ProtocolIpcp = 0x8021;
    public const ushort ProtocolIpv6cp = 0x8057;
    public const ushort ProtocolIp = 0x0021;
    public const ushort ProtocolIpv6 = 0x0057;

    public const byte CodeConfigureRequest = 1;
    public const byte CodeConfigureAck = 2;
    public const byte CodeConfigureNak = 3;
    public const byte CodeConfigureReject = 4;
    public const byte CodeTerminateRequest = 5;
    public const byte CodeTerminateAck = 6;
    public const byte CodeCodeReject = 7;
    public const byte CodeProtocolReject = 8;
    public const byte CodeEchoRequest = 9;
    public const byte CodeEchoReply = 10;
    public const byte CodeDiscardRequest = 11;

    public const byte ChapChallenge = 1;
    public const byte ChapResponse = 2;
    public const byte ChapSuccess = 3;
    public const byte ChapFailure = 4;

    public const byte EapRequest = 1;
    public const byte EapResponse = 2;
    public const byte EapSuccess = 3;
    public const byte EapFailure = 4;

    public const byte EapTypeIdentity = 1;
    public const byte EapTypeNotification = 2;
    public const byte EapTypeNak = 3;
    public const byte EapTypeMsAuth = 26;

    public const byte OptionLcpMru = 1;
    public const byte OptionLcpAuth = 3;
    public const byte ChapAlgorithmMsChapV2 = 0x81;
    public const byte OptionIpcpIp = 0x03;
    public const byte OptionIpcpDns = 0x81;
    public const byte OptionIpv6cpIdentifier = 0x01;

    /// <summary>
    /// Builds a PPP control frame body (code, id, length, payload).
    /// The SSTP/HDLC/protocol header is added by the caller.
    /// </summary>
    public static byte[] ControlFrame(byte code, byte id, ReadOnlySpan<byte> payload)
    {
        var writer = new ByteWriter();
        writer.U8(code);
        writer.U8(id);
        writer.U16((ushort)(4 + payload.Length));
        writer.Bytes(payload);
        return writer.ToArray();
    }

    /// <summary>Parses the code/id/payload of a PPP control frame.</summary>
    public static (byte Code, byte Id, byte[] Payload) ParseControlFrame(ReadOnlySpan<byte> frame)
    {
        var reader = new ByteReader(frame);
        var code = reader.U8();
        var id = reader.U8();
        var length = reader.U16();
        var payloadLength = length - 4;
        if (payloadLength < 0 || payloadLength > reader.Remaining)
        {
            throw new SstpProtocolException("Malformed PPP frame");
        }

        return (code, id, reader.ByteArray(payloadLength));
    }
}
