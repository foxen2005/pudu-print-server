using System.Text;

namespace PuduPrintServer.Services;

public static class EscPos
{
    private static readonly Encoding Cp850 = Encoding.GetEncoding(850);

    public static byte[] Initialize() => [0x1B, 0x40];
    public static byte[] BoldOn()     => [0x1B, 0x45, 0x01];
    public static byte[] BoldOff()    => [0x1B, 0x45, 0x00];
    public static byte[] Center()     => [0x1B, 0x61, 0x01];
    public static byte[] Left()       => [0x1B, 0x61, 0x00];
    public static byte[] BigOn()      => [0x1B, 0x21, 0x30];
    public static byte[] BigOff()     => [0x1B, 0x21, 0x00];
    public static byte[] Cut()        => [0x1D, 0x56, 0x41, 0x03];
    public static byte[] Feed(int n = 1) => Enumerable.Repeat((byte)0x0A, n).ToArray();
    public static byte[] Text(string s)  => Cp850.GetBytes(s);
    public static byte[] Line(string s = "") => Cp850.GetBytes(s + "\n");

    public static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();
}
