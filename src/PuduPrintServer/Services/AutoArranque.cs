using System.IO;
using Microsoft.Win32;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (Jean: "que quede siempre en segundo plano y abra al iniciar Windows, como el SOS"): registra el agente en
/// HKCU\...\Run con `--oculto` (arranca en la bandeja, sin ventana). Solo desde la instalación de Velopack
/// (%LocalAppData%\PuduPrintServer\current\…), nunca desde una compilación de desarrollo. Usa el lanzador de Velopack
/// (%LocalAppData%\PuduPrintServer\PuduPrintServer.exe) si existe, que sobrevive a las actualizaciones.
/// </summary>
public static class AutoArranque
{
    private const string Clave = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Valor = "PuduPrintServer";
    public const string ArgOculto = "--oculto";

    public static void Asegurar()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var carpeta = Path.GetDirectoryName(exe)!;
            if (!string.Equals(Path.GetFileName(carpeta), "current", StringComparison.OrdinalIgnoreCase)) return;

            var lanzador = Path.Combine(Path.GetDirectoryName(carpeta)!, Path.GetFileName(exe));
            var destino = File.Exists(lanzador) ? lanzador : exe;
            var comando = $"\"{destino}\" {ArgOculto}";

            using var k = Registry.CurrentUser.CreateSubKey(Clave);
            if (k.GetValue(Valor) as string == comando) return;
            k.SetValue(Valor, comando);
            LogService.Instance.Info("app", $"Inicio automático con Windows registrado: {comando}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("app", "No se pudo registrar el inicio automático", ex);
        }
    }
}
