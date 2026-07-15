using System.Runtime.InteropServices;

namespace PuduPrintServer.Services;

/// <summary>
/// Envía bytes RAW (ESC/POS) directamente a una impresora instalada en Windows,
/// por su nombre, usando el spooler (winspool). Necesario para impresoras USB que
/// se instalan en puertos de spooler (USB001, USB002, ...) y no exponen un COM.
/// </summary>
public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class DOCINFOW
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType = "";
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true,
        ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level,
        [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOW di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true,
        ExactSpelling = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true,
        ExactSpelling = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true,
        ExactSpelling = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true,
        ExactSpelling = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount,
        out int dwWritten);

    /// <summary>Envía los bytes indicados como trabajo RAW a la impresora nombrada.</summary>
    public static void SendBytes(string printerName, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("Nombre de impresora vacío.", nameof(printerName));

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new Exception(
                $"No se pudo abrir la impresora '{printerName}' (Win32 {Marshal.GetLastWin32Error()}).");

        var ptr = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var di = new DOCINFOW { pDocName = "Pudu Ticket", pDataType = "RAW" };

            if (!StartDocPrinter(hPrinter, 1, di))
                throw new Exception($"StartDocPrinter falló (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new Exception($"StartPagePrinter falló (Win32 {Marshal.GetLastWin32Error()}).");

                if (!WritePrinter(hPrinter, ptr, bytes.Length, out var written) || written != bytes.Length)
                    throw new Exception($"WritePrinter escribió {written}/{bytes.Length} bytes " +
                                        $"(Win32 {Marshal.GetLastWin32Error()}).");

                EndPagePrinter(hPrinter);
            }
            finally { EndDocPrinter(hPrinter); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
            ClosePrinter(hPrinter);
        }
    }
}
