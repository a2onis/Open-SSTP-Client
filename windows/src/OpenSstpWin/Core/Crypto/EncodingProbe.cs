namespace OpenSstpWin.Crypto;

public static class EncodingProbe
{
    public static string Unicode(string value) => Encoding.Unicode.GetBytes(value).Length.ToString();
    public static string Ascii(string value) => Encoding.ASCII.GetBytes(value).Length.ToString();
}
