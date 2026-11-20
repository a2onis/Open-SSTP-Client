namespace OpenSstpWin.Core;

public static class TunnelProbe
{
    public static string Unicode(string value) => System.Text.Encoding.Unicode.GetBytes(value).Length.ToString();
    public static string Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value).Length.ToString();
}
