using Xunit;
using OpenSstpWin.Crypto;

namespace OpenSstpWin.Tests;

public class DebugTests
{
    [Fact]
    public void Probe()
    {
        var lines = new List<string>();
        try { lines.Add("fill=" + ProbeFill()); } catch (Exception e) { lines.Add("fill THREW " + e.GetType().Name + ": " + e.Message); }
        try { lines.Add("resize=" + ProbeResize()); } catch (Exception e) { lines.Add("resize THREW " + e.GetType().Name + ": " + e.Message); }
        try { lines.Add("sha=" + ProbeSha()); } catch (Exception e) { lines.Add("sha THREW " + e.GetType().Name + ": " + e.Message); }
        try { lines.Add("md4=" + ProbeMd4()); } catch (Exception e) { lines.Add("md4 THREW " + e.GetType().Name + ": " + e.Message); }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "probe.log"), string.Join(" || ", lines));
        Assert.True(false, "PROBE: " + string.Join(" || ", lines));
    }

    private static string ProbeFill() { var b = new byte[16]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return "ok"; }
    private static string ProbeResize() { var b = new byte[32]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return "ok"; }
    private static string ProbeSha() { var b = System.Security.Cryptography.SHA1.HashData(new byte[40]); return "len=" + b.Length; }
    private static string ProbeMd4() { var d = Md4.Hash(new byte[10]); return "len=" + d.Length; }
}
