using System.Buffers.Binary;

namespace OpenSstpWin.Protocol;

/// <summary>Raised when a peer sends something we cannot parse.</summary>
public sealed class SstpProtocolException : Exception
{
    public SstpProtocolException(string message) : base(message) { }
}

/// <summary>
/// Big-endian reader over a span. Mirrors the java.nio.ByteBuffer usage of the
/// Android implementation (ByteBuffer defaults to big endian).
/// </summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _pos;

    public ByteReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _buffer.Length - _pos;

    public byte U8()
    {
        Ensure(1);
        return _buffer[_pos++];
    }

    public ushort U16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_pos));
        _pos += 2;
        return value;
    }

    public uint U32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_pos));
        _pos += 4;
        return value;
    }

    public byte PeekU8(int offset = 0)
    {
        Ensure(offset + 1);
        return _buffer[_pos + offset];
    }

    public ushort PeekU16(int offset = 0)
    {
        Ensure(offset + 2);
        return BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_pos + offset));
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Ensure(count);
        var slice = _buffer.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public byte[] ByteArray(int count) => Bytes(count).ToArray();

    public void Skip(int count)
    {
        Ensure(count);
        _pos += count;
    }

    public void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new SstpProtocolException("Malformed packet: " + what);
        }
    }

    private void Ensure(int count)
    {
        if (count < 0 || _pos + count > _buffer.Length)
        {
            throw new SstpProtocolException("Truncated packet");
        }
    }
}

/// <summary>Simple big-endian growable writer.</summary>
public sealed class ByteWriter
{
    private readonly List<byte> _bytes = new(64);

    public int Length => _bytes.Count;

    public void U8(byte value) => _bytes.Add(value);

    public void U16(ushort value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
    }

    public void U32(uint value)
    {
        _bytes.Add((byte)(value >> 24));
        _bytes.Add((byte)(value >> 16));
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            _bytes.Add(b);
        }
    }

    public void Zeros(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _bytes.Add(0);
        }
    }

    public byte[] ToArray() => _bytes.ToArray();
}
