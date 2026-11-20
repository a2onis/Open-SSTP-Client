using System.Security.Cryptography;
using System.Text;
using OpenSstpWin.Protocol;

namespace OpenSstpWin.Crypto;


/// <summary>
/// MS-CHAPv2 client state machine (RFC 2759) including the MPPE key derivation,
/// ported from the Android client's MSCHAPV2Client.
/// </summary>
public sealed class MsChapV2
{
    private static readonly byte[] Magic1 = Hex("4D616769632073657276657220746F20636C69656E74207369676E696E6720636F6E7374616E74");
    private static readonly byte[] Magic2 = Hex("50616420746F206D616B6520697420646F206D6F7265207468616E206F6E6520697465726174696F6E");
    private static readonly byte[] NtPasswordMagic = Hex("5468697320697320746865204D505045204D6173746572204B6579");
    private static readonly byte[] SendKeyMagic = Hex("4F6E2074686520636C69656E7420736964652C2074686973206973207468652073656E64206B65793B206F6E207468652073657276657220736964652C206974206973207468652072656365697665206B65792E");
    private static readonly byte[] ReceiveKeyMagic = Hex("4F6E2074686520636C69656E7420736964652C2074686973206973207468652072656365697665206B65793B206F6E207468652073657276657220736964652C206974206973207468652073656E64206B65792E");
    private static readonly byte[] Pad1 = new byte[40];
    private static readonly byte[] Pad2 = Enumerable.Repeat((byte)0xF2, 40).ToArray();

    private readonly byte[] _serverChallenge = new byte[16];
    private readonly byte[] _clientChallenge = new byte[16];
    private readonly byte[] _serverResponse = new byte[42];
    private readonly byte[] _clientResponse = new byte[24];

    public MsChapV2(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    /// <summary>MPPE send/receive key produced by a successful authentication.</summary>
    public byte[] Hlak { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// Processes a CHAP challenge value and returns the CHAP response value:
    /// peer-challenge (16) + zero (1) + NT-response (24) + flags (1) + zero (7).
    /// </summary>
    public byte[] ProcessChallenge(byte[] challengeValue)
    {
        if (challengeValue.Length < 16)
        {
            throw new SstpProtocolException("Malformed MS-CHAPv2 challenge");
        }

        Array.Copy(challengeValue, 0, _serverChallenge, 0, 16);
        PrepareClientResponse();

        // RFC 2759 2.2: Peer-Challenge(16) || Reserved(8) || NT-Response(24) || Flags(1)
        var response = new byte[49];
        _clientChallenge.CopyTo(response, 0);
        _clientResponse.CopyTo(response, 24);
        response[48] = 0;
        return response;
    }

    /// <summary>
    /// Test seam: processes a challenge with a fixed peer challenge so the
    /// NT-Response can be compared against published vectors.
    /// </summary>
    internal byte[] ProcessChallengeWithClientChallenge(byte[] challengeValue, byte[] clientChallenge)
    {
        Array.Copy(challengeValue, 0, _serverChallenge, 0, 16);
        Array.Copy(clientChallenge, 0, _clientChallenge, 0, 16);
        ComputeClientResponse();
        var response = new byte[49];
        _clientChallenge.CopyTo(response, 0);
        _clientResponse.CopyTo(response, 24);
        return response;
    }

    private void ComputeClientResponse()
    {
        var passwordBytes = Encoding.Unicode.GetBytes(Password);
        var challenge = ChallengeHash();

        var zeroPassHash = new byte[21];
        Md4.Hash(passwordBytes).CopyTo(zeroPassHash, 0);

        for (var i = 0; i < 3; i++)
        {
            var key = AddParity(zeroPassHash.AsSpan(i * 7, 7));
            var block = challenge;
            var encrypted = DesEncryptBlock(key, block);
            encrypted.CopyTo(_clientResponse, i * 8);
        }
    }

    /// <summary>Verifies the "S=..." authenticator response in a success message.</summary>
    public bool VerifyAuthenticator(byte[] successMessage)
    {
        if (successMessage.Length < _serverResponse.Length)
        {
            return false;
        }

        Array.Copy(successMessage, 0, _serverResponse, 0, _serverResponse.Length);
        return CheckServerResponse();
    }

    private byte[] ChallengeHash()
    {
        var userBytes = Encoding.ASCII.GetBytes(Username);
        var input = new byte[16 + 16 + userBytes.Length];
        _clientChallenge.CopyTo(input, 0);
        _serverChallenge.CopyTo(input, 16);
        userBytes.CopyTo(input, 32);

        var digest = SHA1.HashData(input);
        var first = new byte[8];
        Array.Copy(digest, 0, first, 0, 8);
        return first;
    }

    private void PrepareClientResponse()
    {
        RandomNumberGenerator.Fill(_clientChallenge);
        ComputeClientResponse();
    }

    /// <summary>
    /// Encrypts exactly one 8 byte block with single DES (ECB, no padding).
    /// Keys are built explicitly so no provider rejects them for "weak" parity.
    /// </summary>
    private static byte[] DesEncryptBlock(byte[] key, byte[] block)
    {
        using var des = DES.Create();
        des.Key = key;
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        using var encryptor = des.CreateEncryptor(key, null);
        return encryptor.TransformFinalBlock(block, 0, block.Length);
    }

    private static byte[] AddParity(ReadOnlySpan<byte> seven)
    {
        var output = new byte[8];
        ulong holder = 0;
        for (var i = 0; i < 7; i++)
        {
            holder |= (ulong)seven[i] << (8 * (6 - i));
        }

        for (var i = 0; i < 8; i++)
        {
            var b = (int)((holder >> (56 - i * 7)) & 0x7F);
            var parity = IsEvenBits(b) ? 1 : 0;
            output[i] = (byte)((b << 1) | parity);
        }

        return output;
    }

    private static bool IsEvenBits(int value)
    {
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            count += value & 1;
            value >>= 1;
        }

        return count % 2 == 0;
    }

    private bool CheckServerResponse()
    {
        var passwordBytes = Encoding.Unicode.GetBytes(Password);
        var challenge = ChallengeHash();

        var buffer = new byte[16 + 24 + Magic1.Length];
        Md4.Hash(Md4.Hash(passwordBytes)).CopyTo(buffer, 0);
        _clientResponse.CopyTo(buffer, 16);
        Magic1.CopyTo(buffer, 40);
        var tempDigest = SHA1.HashData(buffer);

        var second = new byte[tempDigest.Length + challenge.Length + Magic2.Length];
        tempDigest.CopyTo(second, 0);
        challenge.CopyTo(second, 20);
        Magic2.CopyTo(second, 28);
        var digest = SHA1.HashData(second);

        var expected = Encoding.ASCII.GetBytes("S=" + Convert.ToHexString(digest));
        return expected.AsSpan().SequenceEqual(_serverResponse);
    }

    /// <summary>Derives the MPPE HLAK used for the SSTP compound MAC.</summary>
    public void PrepareHlak()
    {
        var passwordBytes = Encoding.Unicode.GetBytes(Password);

        var master = new byte[16 + 24 + NtPasswordMagic.Length];
        Md4.Hash(Md4.Hash(passwordBytes)).CopyTo(master, 0);
        _clientResponse.CopyTo(master, 16);
        NtPasswordMagic.CopyTo(master, 40);
        var masterKey = SHA1.HashData(master).AsSpan(0, 16).ToArray();

        Hlak = new byte[32];

        var send = new byte[16 + Pad1.Length + SendKeyMagic.Length + Pad2.Length];
        masterKey.CopyTo(send, 0);
        Pad1.CopyTo(send, 16);
        SendKeyMagic.CopyTo(send, 16 + Pad1.Length);
        Pad2.CopyTo(send, 16 + Pad1.Length + SendKeyMagic.Length);
        SHA1.HashData(send).CopyTo(Hlak, 0);

        var receive = new byte[16 + Pad1.Length + ReceiveKeyMagic.Length + Pad2.Length];
        masterKey.CopyTo(receive, 0);
        Pad1.CopyTo(receive, 16);
        ReceiveKeyMagic.CopyTo(receive, 16 + Pad1.Length);
        Pad2.CopyTo(receive, 16 + Pad1.Length + ReceiveKeyMagic.Length);
        SHA1.HashData(receive).CopyTo(Hlak, 16);
    }

    private static byte[] Hex(string text) => Convert.FromHexString(text);
}
