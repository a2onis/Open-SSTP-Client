namespace OpenSstpWin.Protocol;

/// <summary>LCP / IPCP / IPv6CP option packing (type-length-value lists).</summary>
public sealed class PppOption
{
    public required byte Type { get; init; }

    /// <summary>Raw option payload, not including the type and length octets.</summary>
    public byte[] Value { get; init; } = Array.Empty<byte>();

    public int EncodedLength => 2 + Value.Length;

    public void WriteTo(ByteWriter writer)
    {
        writer.U8(Type);
        writer.U8((byte)EncodedLength);
        writer.Bytes(Value);
    }

    public static List<PppOption> ParseList(ReadOnlySpan<byte> data)
    {
        var options = new List<PppOption>();
        var reader = new ByteReader(data);
        while (reader.Remaining > 0)
        {
            if (reader.Remaining < 2)
            {
                throw new SstpProtocolException("Truncated PPP option");
            }

            var type = reader.U8();
            var length = reader.U8();
            if (length < 2 || length - 2 > reader.Remaining)
            {
                throw new SstpProtocolException("Malformed PPP option");
            }

            options.Add(new PppOption { Type = type, Value = reader.ByteArray(length - 2) });
        }

        return options;
    }

    public static byte[] WriteList(IEnumerable<PppOption> options)
    {
        var writer = new ByteWriter();
        foreach (var option in options)
        {
            option.WriteTo(writer);
        }

        return writer.ToArray();
    }

    public static PppOption MakeUInt16(byte type, ushort value) => new()
    {
        Type = type,
        Value = new[] { (byte)(value >> 8), (byte)value },
    };

    public static PppOption MakeBytes(byte type, ReadOnlySpan<byte> value) => new()
    {
        Type = type,
        Value = value.ToArray(),
    };

    public static ushort ReadUInt16(PppOption option)
    {
        var reader = new ByteReader(option.Value);
        return reader.U16();
    }
}
