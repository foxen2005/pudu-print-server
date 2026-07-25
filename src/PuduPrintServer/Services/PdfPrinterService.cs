using System.Diagnostics;
using System.IO;

namespace PuduPrintServer.Services;

/// <summary>
/// Imprime un PDF de forma silenciosa usando SumatraPDF (bundleado junto al .exe).
/// Usado para el ticket de caja, que viaja como PDF vectorial (con timbre TED)
/// generado por POS Matic — a diferencia de las comandas, que son ESC/POS crudo.
///
/// Evita los bugs conocidos del agente Electron "Bicom":
///  - ruta ABSOLUTA a SumatraPDF (no depende del PATH),
///  - archivo temporal ÚNICO por trabajo (no borra una carpeta compartida),
///  - se ESPERA a que el PDF termine de escribirse antes de imprimir,
///  - se chequea el ExitCode del proceso (fallo detectable, no silencioso).
/// </summary>
public class PdfPrinterService
{
    private static string SumatraPath => Path.Combine(AppContext.BaseDirectory, "SumatraPDF.exe");

    public async Task PrintPdfAsync(byte[] pdf, string printerName)
    {
        if (pdf.Length == 0)
            throw new ArgumentException("PDF vacío.");
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("Nombre de impresora vacío.");
        if (!File.Exists(SumatraPath))
            throw new FileNotFoundException(
                $"No se encontró SumatraPDF.exe en {SumatraPath}. Debe bundlearse junto al ejecutable (ver tools/README.md).");

        var tmp = Path.Combine(Path.GetTempPath(), $"pudu-ticket-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(tmp, pdf);

            var psi = new ProcessStartInfo
            {
                FileName               = SumatraPath,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardError  = true,
            };
            // ArgumentList escapa cada argumento por separado (nombres con espacios OK).
            psi.ArgumentList.Add("-print-to");
            psi.ArgumentList.Add(printerName);
            psi.ArgumentList.Add("-silent");
            psi.ArgumentList.Add("-print-settings");
            psi.ArgumentList.Add("noscale");
            psi.ArgumentList.Add(tmp);

            using var proc = Process.Start(psi)
                ?? throw new Exception("No se pudo iniciar SumatraPDF.");

            var stderrTask = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new Exception("SumatraPDF no respondió en 30 segundos.");
            }

            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new Exception($"SumatraPDF terminó con código {proc.ExitCode}. {stderr}".Trim());
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }
}
