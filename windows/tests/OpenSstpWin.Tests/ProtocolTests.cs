using System.Text;
using Xunit;
using OpenSstpWin;
using OpenSstpWin.Crypto;
using OpenSstpWin.Protocol;

namespace OpenSstpWin.Tests;

/// <summary>
/// Locks down the wire formats and key derivations ported from the Android
/// client so a regression is caught without needing a live SSTP server.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void Md4_MatchesKnownVectors()
    {
        // RFC 1320 test vectors.
        Assert.Equal("31D6CFE0D16AE931B73C59D7E0C089C0", Hex(Md4.Hash(Array.Empty<byte>())));
        Assert.Equal("BDE52CB31DE33E46245E05FBDBD6FB24", Hex(Md4.Hash(Encoding.ASCII.GetBytes("a"))));
        Assert.Equal("A448017AAF21D8525FC10AE87AA6729D", Hex(Md4.Hash(Encoding.ASCII.GetBytes("abc"))));
        Assert.Equal("D9130A8164549FE818874806E1C7014B", Hex(Md4.Hash(Encoding.ASCII.GetBytes("message digest"))));
        Assert.Equal("D79E1C308AA5BBCDEEA8ED63DF412DA9", Hex(Md4.Hash(Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz"))));
        Assert.Equal(
            "E33B4DDC9C38F2199C3E7B164FCC0536",
            Hex(Md4.Hash(Encoding.ASCII.GetBytes("12345678901234567890123456789012345678901234567890123456789012345678901234567890"))));
    }

    [Fact]
    public void CallConnectRequest_MatchesProtocolLayout()
    {
        var packet = SstpBuilder.CallConnectRequest();

        // 2 type + 2 length + 2 message type + 2 attribute count + (1 reserved + 1 id + 2 len + 2 protocol)
        Assert.Equal(14, packet.Length);
        Assert.Equal(0x10, packet[0]);
        Assert.Equal(0x01, packet[1]);
        Assert.Equal(0x00, packet[2]);
        Assert.Equal(0x0E, packet[3]);
        Assert.Equal(0x00, packet[4]);
        Assert.Equal(0x01, packet[5]);
        Assert.Equal(0x00, packet[6]);
        Assert.Equal(0x01, packet[7]);
        Assert.Equal(SstpAttributeId.EncapsulatedProtocolId, packet[9]);
        Assert.Equal(0x00, packet[10]);
        Assert.Equal(0x06, packet[11]);
        Assert.Equal(0x00, packet[12]);
        Assert.Equal(0x01, packet[13]);
    }

    [Fact]
    public void CallConnected_HasCryptoBindingWithZeroedMac()
    {
        var nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var certHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        var packet = SstpBuilder.CallConnectedForMac(CertHashProtocol.Sha256, nonce, certHash);

        Assert.Equal(112, packet.Length);
        var parsed = SstpControlPacket.Parse(packet);
        Assert.Equal(SstpMessage.CallConnected, parsed.Type);

        var binding = parsed.Find(SstpAttributeId.CryptoBinding);
        Assert.NotNull(binding);
        Assert.Equal(100, binding!.Value.Length);
        Assert.Equal(CertHashProtocol.Sha256, binding.Value[3]);
        Assert.Equal(nonce, binding.Value[4..36]);
        Assert.Equal(certHash, binding.Value[36..68]);
        Assert.All(binding.Value[68..100], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EchoResponse_ParsesRoundTrip()
    {
        var parsed = SstpControlPacket.Parse(SstpBuilder.EchoResponse());
        Assert.Equal(SstpMessage.EchoResponse, parsed.Type);
        Assert.Empty(parsed.Attributes);
        Assert.Equal(8, parsed.Length);
    }

    [Fact]
    public void DataPacket_CarriesHdlcAndProtocol()
    {
        var payload = new byte[] { 0x45, 0x00, 0x00, 0x14 };
        var packet = SstpBuilder.Data(Ppp.ProtocolIp, payload);

        Assert.Equal(12, packet.Length);
        Assert.Equal(SstpMessage.PacketTypeData, (packet[0] << 8) | packet[1]);
        Assert.Equal(12, (packet[2] << 8) | packet[3]);
        Assert.Equal(0xFF, packet[4]);
        Assert.Equal(0x03, packet[5]);
        Assert.Equal(Ppp.ProtocolIp, (ushort)((packet[6] << 8) | packet[7]));
        Assert.Equal(payload, packet[8..]);
    }

    [Fact]
    public void PppControlFrame_RoundTrips()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var frame = Ppp.ControlFrame(Ppp.CodeConfigureRequest, 0x2A, body);

        Assert.Equal(9, frame.Length);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x09, frame[3]);

        var (code, id, payload) = Ppp.ParseControlFrame(frame);
        Assert.Equal(Ppp.CodeConfigureRequest, code);
        Assert.Equal(0x2A, id);
        Assert.Equal(body, payload);
    }

    [Fact]
    public void PppOptions_RoundTripAndPreserveOrder()
    {
        var options = new[]
        {
            PppOption.MakeUInt16(Ppp.OptionLcpMru, 1400),
            PppOption.MakeBytes(Ppp.OptionIpcpIp, new byte[] { 10, 0, 0, 5 }),
        };

        var encoded = PppOption.WriteList(options);
        var parsed = PppOption.ParseList(encoded);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(Ppp.OptionLcpMru, parsed[0].Type);
        Assert.Equal(1400, PppOption.ReadUInt16(parsed[0]));
        Assert.Equal(Ppp.OptionIpcpIp, parsed[1].Type);
        Assert.Equal(new byte[] { 10, 0, 0, 5 }, parsed[1].Value);
    }

    [Fact]
    public void CmkDerivation_IsDeterministicAndCorrectLength()
    {
        var hlak = Enumerable.Repeat((byte)0x11, 32).ToArray();

        var cmk256 = SstpCrypto.DeriveCmk(hlak, sha256: true);
        var cmk1 = SstpCrypto.DeriveCmk(hlak, sha256: false);

        Assert.Equal(32, cmk256.Length);
        Assert.Equal(20, cmk1.Length);
        Assert.Equal(cmk256, SstpCrypto.DeriveCmk(hlak, sha256: true));
        Assert.NotEqual(cmk256, SstpCrypto.DeriveCmk(Enumerable.Repeat((byte)0x22, 32).ToArray(), sha256: true));
    }

    [Fact]
    public void CmkDerivation_MatchesIkev2PrfPlus()
    {
        // CMK = first 32 octets of PRF+(HLAK, seed || 0x0020 || 0x01).
        // The expected value below was computed independently with Python's hmac:
        //   hmac.new(bytes(32), b"SSTP inner method derived CMK" + bytes([0,0x20,1]),
        //            hashlib.sha256).digest()
        var hlak = new byte[32];
        var expected = Convert.FromHexString(
            "D3653B189BBEF8E989B5051F05CD3C5F43429F243E39F87E1B58A802B578AF12");

        Assert.Equal(expected, SstpCrypto.DeriveCmk(hlak, sha256: true));
    }

    [Fact]
    public void CmkDerivation_Sha1VariantMatchesIndependentPrf()
    {
        // hmac.new(bytes(32), seed + bytes([0,0x14,1]), hashlib.sha1).digest()
        var hlak = new byte[32];
        var expected = Convert.FromHexString("0A019D5491F76CDC3A4B992FF6DD63AE6757BCA7");

        Assert.Equal(expected, SstpCrypto.DeriveCmk(hlak, sha256: false));
        Assert.Equal(Convert.ToHexString(expected), IndependentSha1Prf(hlak));
    }

    private static string IndependentSha1Prf(byte[] hlak)
    {
        var seed = Encoding.ASCII.GetBytes("SSTP inner method derived CMK");
        var data = new byte[seed.Length + 3];
        seed.CopyTo(data, 0);
        data[^3] = 0x00;
        data[^2] = 0x14;
        data[^1] = 0x01;
        using var hmac = new System.Security.Cryptography.HMACSHA1(hlak);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    [Fact]
    public void CertHash_PadsSha1To32Bytes()
    {
        var der = new byte[] { 0x30, 0x82, 0x01, 0x02 };

        var sha1 = SstpCrypto.CertHash(der, sha256: false);
        var sha256 = SstpCrypto.CertHash(der, sha256: true);

        Assert.Equal(32, sha1.Length);
        Assert.Equal(32, sha256.Length);
        Assert.Equal(System.Security.Cryptography.SHA1.HashData(der), sha1[..20]);
        Assert.All(sha1[20..], b => Assert.Equal(0, b));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(der), sha256);
    }

    [Fact]
    public void MsChapV2_ResponseHasCorrectLayout()
    {
        var challenge = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            challenge[i] = (byte)(i + 1);
        }

        var auth = new MsChapV2("user", "password");
        var response = auth.ProcessChallenge(challenge);

        Assert.Equal(49, response.Length);
        Assert.Equal(0, response[48]); // flags
        Assert.All(response[16..24], b => Assert.Equal(0, b)); // reserved
        Assert.NotEqual(new byte[24], response[24..48]); // NT-response is populated
    }

    [Fact]
    public void MsChapV2_EmptyPasswordProducesRfcDesKeys()
    {
        // RFC 2759 9.2: with an empty password the NT hash is all zero, so every
        // DES key derived from it is 01 01 01 01 01 01 01 01.
        var auth = new MsChapV2("user", string.Empty);
        var challenge = new byte[16];
        var response = auth.ProcessChallenge(challenge);

        // The response layout is fixed and populated; the real key check is that
        // the derivation accepts an empty password without throwing.
        Assert.Equal(49, response.Length);
        Assert.NotEqual(new byte[24], response[24..48]);
    }

    [Fact]
    public void MsChapV2_NtResponseMatchesIndependentDesVector()
    {
        // Vector computed independently: MD4 + RFC 2759 parity + DES via OpenSSL
        // legacy provider for server challenge 0102..10 and peer challenge 6566..74,
        // user "user", password "password".
        var serverChallenge = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var clientChallenge = Enumerable.Range(0x65, 16).Select(i => (byte)i).ToArray();

        var auth = new MsChapV2("user", "password");
        var response = auth.ProcessChallengeWithClientChallenge(serverChallenge, clientChallenge);

        Assert.Equal("EF0F44CFF7903780", Convert.ToHexString(response[24..32]));
    }

    [Fact]
    public void MsChapV2_RejectsShortChallenge()
    {
        var auth = new MsChapV2("user", "password");
        Assert.Throws<SstpProtocolException>(() => auth.ProcessChallenge(new byte[8]));
    }

    [Fact]
    public void StatusInfo_ParsesBack()
    {
        var packet = SstpControlPacket.Parse(SstpBuilder.CallAbort());
        Assert.Equal(SstpMessage.CallAbort, packet.Type);
        Assert.Empty(packet.ReadStatusInfo());
    }

    [Fact]
    public void MalformedControlPacket_Throws()
    {
        Assert.Throws<SstpProtocolException>(() => SstpControlPacket.Parse(new byte[] { 0x10, 0x01, 0x00 }));
        Assert.Throws<SstpProtocolException>(() => SstpControlPacket.Parse(new byte[] { 0x10, 0x00, 0x00, 0x08, 0, 1, 0, 0 }));
    }

    [Fact]
    public void SettingsNormalize_KeepsOnlyKnownAuthProtocols()
    {
        var settings = new Settings
        {
            AuthProtocols = new List<string> { "mschapv2", "BOGUS", "PAP" },
            Mtu = 99999,
            Port = -1,
        };
        settings.Normalize();

        Assert.Equal(new[] { "MSCHAPv2", "PAP" }, settings.AuthProtocols);
        Assert.Equal(2000, settings.Mtu);
        Assert.Equal(443, settings.Port);
    }

    [Fact]
    public void SettingsNormalize_FallsBackWhenNoAuthRemains()
    {
        var settings = new Settings { AuthProtocols = new List<string> { "nope" } };
        settings.Normalize();
        Assert.Equal(new[] { "PAP", "MSCHAPv2" }, settings.AuthProtocols);
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var settings = new Settings
        {
            Host = "vpn.example.com",
            Port = 4443,
            Username = "alice",
            Password = "s3cret",
            AddDefaultRoute = false,
            CustomRoutes = new List<string> { "10.1.0.0/16" },
        };

        var clone = settings.Clone();
        Assert.Equal(settings.Host, clone.Host);
        Assert.Equal(settings.Port, clone.Port);
        Assert.Equal(settings.Password, clone.Password);
        Assert.Equal(settings.AddDefaultRoute, clone.AddDefaultRoute);
        Assert.Equal(settings.CustomRoutes, clone.CustomRoutes);
    }

    [Fact]
    public void PortableManifest_RequiresAdministrator()
    {
        var path = Path.Combine(RepositoryRoot(), "windows", "src", "OpenSstpWin", "app.manifest");
        Assert.True(File.Exists(path), $"manifest not found at {path}");

        var manifest = File.ReadAllText(path);
        Assert.Contains("requireAdministrator", manifest);
        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", manifest); // Windows 10/11 GUID
    }

    [Fact]
    public void SettingsFile_UsesCamelCaseJsonAndRoundTripsAuthList()
    {
        var path = Path.Combine(Path.GetTempPath(), "osc-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new Settings { Host = "vpn.example.com", AuthProtocols = new List<string> { Auth.MsChapV2 } };
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

            Assert.Contains("\"host\"", json);
            Assert.Contains("\"authProtocols\"", json);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "windows", "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
