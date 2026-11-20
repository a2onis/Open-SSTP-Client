namespace OpenSstpWin.Protocol;

/// <summary>MS-SSTP control message types (section 2.1 of the specification).</summary>
public static class SstpMessage
{
    public const ushort PacketTypeData = 0x1000;
    public const ushort PacketTypeControl = 0x1001;

    public const ushort CallConnectRequest = 1;
    public const ushort CallConnectAck = 2;
    public const ushort CallConnectNak = 3;
    public const ushort CallConnected = 4;
    public const ushort CallAbort = 5;
    public const ushort CallDisconnect = 6;
    public const ushort CallDisconnectAck = 7;
    public const ushort EchoRequest = 8;
    public const ushort EchoResponse = 9;

    public static string Name(ushort type) => type switch
    {
        CallConnectRequest => "CALL_CONNECT_REQUEST",
        CallConnectAck => "CALL_CONNECT_ACK",
        CallConnectNak => "CALL_CONNECT_NAK",
        CallConnected => "CALL_CONNECTED",
        CallAbort => "CALL_ABORT",
        CallDisconnect => "CALL_DISCONNECT",
        CallDisconnectAck => "CALL_DISCONNECT_ACK",
        EchoRequest => "ECHO_REQUEST",
        EchoResponse => "ECHO_RESPONSE",
        _ => $"UNKNOWN({type})",
    };
}

/// <summary>SSTP control message attribute identifiers.</summary>
public static class SstpAttributeId
{
    public const byte NoError = 0;
    public const byte EncapsulatedProtocolId = 1;
    public const byte StatusInfo = 2;
    public const byte CryptoBinding = 3;
    public const byte CryptoBindingRequest = 4;
}

/// <summary>Certificate hash protocol identifiers used by the crypto binding.</summary>
public static class CertHashProtocol
{
    public const byte Sha1 = 1;
    public const byte Sha256 = 2;
}

public sealed class SstpStatusInfo
{
    public byte TargetId { get; init; }
    public uint Status { get; init; }
    public byte[] Holder { get; init; } = Array.Empty<byte>();
}

/// <summary>A parsed SSTP control packet.</summary>
public sealed class SstpControlPacket
{
    public ushort Type { get; init; }
    public List<SstpAttribute> Attributes { get; } = new();
    public int Length { get; init; }

    public SstpAttribute? Find(byte id) => Attributes.FirstOrDefault(a => a.Id == id);

    public List<SstpStatusInfo> ReadStatusInfo()
    {
        var result = new List<SstpStatusInfo>();
        foreach (var attribute in Attributes.Where(a => a.Id == SstpAttributeId.StatusInfo))
        {
            var reader = new ByteReader(attribute.Value);
            reader.Expect(attribute.Value.Length >= 8, "status-info");
            reader.Skip(3);
            var targetId = reader.U8();
            var status = reader.U32();
            result.Add(new SstpStatusInfo
            {
                TargetId = targetId,
                Status = status,
                Holder = reader.Remaining > 0 ? reader.ByteArray(reader.Remaining) : Array.Empty<byte>(),
            });
        }

        return result;
    }

    public static SstpControlPacket Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new ByteReader(packet);
        reader.Expect(reader.U16() == SstpMessage.PacketTypeControl, "control packet type");
        var length = reader.U16();
        var type = reader.U16();
        var attributeCount = reader.U16();
        reader.Expect(length == packet.Length, "control packet length");

        var result = new SstpControlPacket { Type = type, Length = length };
        for (var i = 0; i < attributeCount; i++)
        {
            reader.Expect(reader.Remaining >= 4, "attribute header");
            reader.Skip(1); // reserved
            var id = reader.U8();
            var attributeLength = reader.U16();
            reader.Expect(attributeLength >= 4, "attribute length");
            result.Attributes.Add(new SstpAttribute
            {
                Id = id,
                Value = reader.ByteArray(attributeLength - 4),
            });
        }

        return result;
    }
}

public sealed class SstpAttribute
{
    public required byte Id { get; init; }
    public byte[] Value { get; init; } = Array.Empty<byte>();

    public static SstpAttribute FromBytes(byte id, ReadOnlySpan<byte> value) => new()
    {
        Id = id,
        Value = value.ToArray(),
    };
}

/// <summary>Builders for the SSTP packets this client sends.</summary>
internal static class SstpBuilder
{
    private const ushort EncapsulatedProtocolPpp = 1;

    private static byte[] Attribute(byte id, ReadOnlySpan<byte> value)
    {
        var writer = new ByteWriter();
        writer.U8(0); // reserved
        writer.U8(id);
        writer.U16((ushort)(4 + value.Length));
        writer.Bytes(value);
        return writer.ToArray();
    }

    private static byte[] Control(ushort type, params byte[][] attributes)
    {
        var body = new ByteWriter();
        foreach (var attribute in attributes)
        {
            body.Bytes(attribute);
        }

        var encodedBody = body.ToArray();
        var writer = new ByteWriter();
        writer.U16(SstpMessage.PacketTypeControl);
        writer.U16((ushort)(8 + encodedBody.Length));
        writer.U16(type);
        writer.U16((ushort)attributes.Length);
        writer.Bytes(encodedBody);
        return writer.ToArray();
    }

    public static byte[] EncapsulatedProtocolId()
    {
        var writer = new ByteWriter();
        writer.U16(EncapsulatedProtocolPpp);
        return Attribute(SstpAttributeId.EncapsulatedProtocolId, writer.ToArray());
    }

    public static byte[] CallConnectRequest() => Control(SstpMessage.CallConnectRequest, EncapsulatedProtocolId());

    public static byte[] CryptoBinding(byte hashProtocol, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> certHash, ReadOnlySpan<byte> compoundMac)
    {
        var writer = new ByteWriter();
        writer.Zeros(3);
        writer.U8(hashProtocol);
        writer.Bytes(nonce);
        writer.Bytes(certHash);
        writer.Bytes(compoundMac);
        return Attribute(SstpAttributeId.CryptoBinding, writer.ToArray());
    }

    public static byte[] StatusInfo(byte targetId, uint status, ReadOnlySpan<byte> holder)
    {
        var writer = new ByteWriter();
        writer.Zeros(3);
        writer.U8(targetId);
        writer.U32(status);
        writer.Bytes(holder);
        return Attribute(SstpAttributeId.StatusInfo, writer.ToArray());
    }

    public static byte[] CallConnected(byte hashProtocol, ReadOnlySpan<byte> binding, ReadOnlySpan<byte> compoundMac)
    {
        // The MAC is computed with the field zeroed (MS-SSTP 3.2.5.2), so it is
        // written last; callers pass the value they want embedded.
        var attribute = CryptoBinding(hashProtocol, binding[..32], binding[32..64], compoundMac);
        return Control(SstpMessage.CallConnected, attribute);
    }

    /// <summary>
    /// Builds CALL_CONNECTED twice-safe: returns the packet with a zeroed
    /// compound MAC, which is the exact byte range fed to the HMAC.
    /// </summary>
    public static byte[] CallConnectedForMac(byte hashProtocol, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> certHash)
        => Control(SstpMessage.CallConnected, CryptoBinding(hashProtocol, nonce, certHash, new byte[32]));

    public static byte[] CallConnectedWithMac(byte hashProtocol, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> certHash, ReadOnlySpan<byte> compoundMac)
        => Control(SstpMessage.CallConnected, CryptoBinding(hashProtocol, nonce, certHash, compoundMac));

    public static byte[] EchoResponse() => Control(SstpMessage.EchoResponse);

    public static byte[] CallDisconnect() => Control(SstpMessage.CallDisconnect);

    public static byte[] CallDisconnectAck() => Control(SstpMessage.CallDisconnectAck);

    public static byte[] CallAbort() => Control(SstpMessage.CallAbort);

    /// <summary>Wraps a PPP frame into an SSTP data packet.</summary>
    public static byte[] Data(ushort protocol, ReadOnlySpan<byte> pppPayload)
    {
        var writer = new ByteWriter();
        writer.U16(SstpMessage.PacketTypeData);
        writer.U16((ushort)(8 + pppPayload.Length));
        writer.U16(Ppp.HdlcHeader);
        writer.U16(protocol);
        writer.Bytes(pppPayload);
        return writer.ToArray();
    }
}
