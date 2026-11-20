using System.Security.Cryptography;
using Xunit;

namespace OpenSstpWin.Tests;

public class StepProbeTests
{
    [Fact]
    public void Steps()
    {
        var steps = new List<string>();
        try
        {
            steps.Add("md4");
            var hash = OpenSstpWin.Crypto.Md4.Hash(System.Text.Encoding.Unicode.GetBytes("password"));
            steps.Add("md4 ok " + hash.Length);

            var zh = new byte[21];
            hash.CopyTo(zh, 0);
            steps.Add("copy ok");

            var seven = zh.AsSpan(7, 7);
            steps.Add("span ok " + seven.Length);

            var key = Parity(seven);
            steps.Add("parity ok " + Convert.ToHexString(key));

            var block = new byte[8];
            steps.Add("block ok");

            using var des = DES.Create();
            steps.Add("des created");
            des.Key = key;
            steps.Add("key set");
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            steps.Add("mode set");
            using var enc = des.CreateEncryptor(key, null);
            steps.Add("encryptor created");
            var outBytes = enc.TransformFinalBlock(block, 0, 8);
            steps.Add("encrypted " + Convert.ToHexString(outBytes));
        }
        catch (Exception e)
        {
            steps.Add("THREW " + e.GetType().Name + " @ " + e.StackTrace);
        }

        Assert.True(false, "STEPS: " + string.Join(" | ", steps));
    }

    private static byte[] Parity(ReadOnlySpan<byte> seven)
    {
        var output = new byte[8];
        ulong holder = 0;
        for (var i = 0; i < 7; i++) holder |= (ulong)seven[i] << (8 * (6 - i));
        for (var i = 0; i < 8; i++)
        {
            var b = (int)((holder >> (56 - i * 7)) & 0x7F);
            var count = 0; var v = b;
            for (var k = 0; k < 8; k++) { count += v & 1; v >>= 1; }
            output[i] = (byte)((b << 1) | (count % 2 == 0 ? 1 : 0));
        }
        return output;
    }
}
