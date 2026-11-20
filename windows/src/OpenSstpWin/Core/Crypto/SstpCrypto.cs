using System.Security.Cryptography;
using System.Text;

namespace OpenSstpWin.Crypto;

/// <summary>
/// SSTP crypto binding key derivation (MS-SSTP 3.2.5.2): the compound MAC key is
/// PRF+(HLAK, "SSTP inner method derived CMK", 32).
/// </summary>
public static class SstpCrypto
{
    private static readonly byte[] CmkSeed = Encoding.ASCII.GetBytes("SSTP inner method derived CMK");

    /// <summary>
    /// Derives the compound MAC key (MS-SSTP 3.2.5.2.x):
    /// CMK = PRF+(HLAK, seed || CMK-length || 0x01, key length).
    /// The 16-bit length field is written big-endian as the specification shows.
    /// </summary>
    public static byte[] DeriveCmk(byte[] hlak, bool sha256)
    {
        var keyLength = sha256 ? 32 : 20;

        var data = new byte[CmkSeed.Length + 3];
        CmkSeed.CopyTo(data, 0);
        data[CmkSeed.Length] = (byte)(keyLength >> 8);
        data[CmkSeed.Length + 1] = (byte)keyLength;
        data[CmkSeed.Length + 2] = 0x01;

        using var hmac = sha256 ? (HMAC)new HMACSHA256(hlak) : new HMACSHA1(hlak);

        var output = new byte[keyLength];
        var produced = 0;
        var counter = (byte)1;
        var block = new byte[data.Length + 1];
        while (produced < keyLength)
        {
            data.CopyTo(block, 0);
            block[^1] = counter;
            var hash = hmac.ComputeHash(block);
            var take = Math.Min(hash.Length, keyLength - produced);
            Array.Copy(hash, 0, output, produced, take);
            produced += take;
            counter++;
        }

        return output;
    }

    public static byte[] CompoundMac(byte[] cmk, ReadOnlySpan<byte> packet, bool sha256)
    {
        using var hmac = sha256 ? (HMAC)new HMACSHA256(cmk) : new HMACSHA1(cmk);
        return hmac.ComputeHash(packet.ToArray());
    }

    public static byte[] CertHash(byte[] certificateDer, bool sha256)
    {
        var digest = sha256 ? SHA256.HashData(certificateDer) : SHA1.HashData(certificateDer);
        var result = new byte[32];
        digest.CopyTo(result, 0);
        return result;
    }
}
