using System.Buffers.Binary;

namespace OpenSstpWin.Crypto;

/// <summary>
/// MD4 (RFC 1320). .NET has no MD4 implementation and MS-CHAPv2 requires it,
/// so this is a direct port of the MD4 routine in the Android client.
/// </summary>
public static class Md4
{
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var inputLength = input.Length;
        var remainder = inputLength % 64;
        var padLength = remainder >= 56 ? 64 - (remainder - 56) : 56 - remainder;
        var totalLength = inputLength + padLength + 8;

        var buffer = new byte[totalLength];
        input.CopyTo(buffer);
        buffer[inputLength] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(totalLength - 8), (ulong)(8L * inputLength));

        uint a = 0x67452301;
        uint b = 0xEFCDAB89;
        uint c = 0x98BADCFE;
        uint d = 0x10325476;

        var x = new uint[16];

        for (var block = 0; block < totalLength / 64; block++)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(block * 64 + i * 4));
            }

            uint aa = a, bb = b, cc = c, dd = d;

            // Round 1
            a = R1(a, b, c, d, x[0], 3);
            d = R1(d, a, b, c, x[1], 7);
            c = R1(c, d, a, b, x[2], 11);
            b = R1(b, c, d, a, x[3], 19);
            a = R1(a, b, c, d, x[4], 3);
            d = R1(d, a, b, c, x[5], 7);
            c = R1(c, d, a, b, x[6], 11);
            b = R1(b, c, d, a, x[7], 19);
            a = R1(a, b, c, d, x[8], 3);
            d = R1(d, a, b, c, x[9], 7);
            c = R1(c, d, a, b, x[10], 11);
            b = R1(b, c, d, a, x[11], 19);
            a = R1(a, b, c, d, x[12], 3);
            d = R1(d, a, b, c, x[13], 7);
            c = R1(c, d, a, b, x[14], 11);
            b = R1(b, c, d, a, x[15], 19);

            // Round 2
            a = R2(a, b, c, d, x[0], 3);
            d = R2(d, a, b, c, x[4], 5);
            c = R2(c, d, a, b, x[8], 9);
            b = R2(b, c, d, a, x[12], 13);
            a = R2(a, b, c, d, x[1], 3);
            d = R2(d, a, b, c, x[5], 5);
            c = R2(c, d, a, b, x[9], 9);
            b = R2(b, c, d, a, x[13], 13);
            a = R2(a, b, c, d, x[2], 3);
            d = R2(d, a, b, c, x[6], 5);
            c = R2(c, d, a, b, x[10], 9);
            b = R2(b, c, d, a, x[14], 13);
            a = R2(a, b, c, d, x[3], 3);
            d = R2(d, a, b, c, x[7], 5);
            c = R2(c, d, a, b, x[11], 9);
            b = R2(b, c, d, a, x[15], 13);

            // Round 3
            a = R3(a, b, c, d, x[0], 3);
            d = R3(d, a, b, c, x[8], 9);
            c = R3(c, d, a, b, x[4], 11);
            b = R3(b, c, d, a, x[12], 15);
            a = R3(a, b, c, d, x[2], 3);
            d = R3(d, a, b, c, x[10], 9);
            c = R3(c, d, a, b, x[6], 11);
            b = R3(b, c, d, a, x[14], 15);
            a = R3(a, b, c, d, x[1], 3);
            d = R3(d, a, b, c, x[9], 9);
            c = R3(c, d, a, b, x[5], 11);
            b = R3(b, c, d, a, x[13], 15);
            a = R3(a, b, c, d, x[3], 3);
            d = R3(d, a, b, c, x[11], 9);
            c = R3(c, d, a, b, x[7], 11);
            b = R3(b, c, d, a, x[15], 15);

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }

    private static uint Rotate(uint value, int shift) => (value << shift) | (value >> (32 - shift));

    private static uint R1(uint a, uint b, uint c, uint d, uint x, int s)
        => Rotate(a + ((b & c) | (~b & d)) + x, s);

    private static uint R2(uint a, uint b, uint c, uint d, uint x, int s)
        => Rotate(a + ((b & c) | (b & d) | (c & d)) + x + 0x5A827999u, s);

    private static uint R3(uint a, uint b, uint c, uint d, uint x, int s)
        => Rotate(a + (b ^ c ^ d) + x + 0x6ED9EBA1u, s);
}
