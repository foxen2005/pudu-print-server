using System.IO;
using System.Text;

namespace PuduPrintServer.Services;

/// <summary>
/// Log a archivo de texto, un archivo por día, para ver qué va pasando en el agente
/// (comandas, tickets, caídas de red, errores) sin depender de que la ventana esté abierta.
///
/// Carpeta: con Velopack el exe vive en %LocalAppData%\PuduPrintServer\current\ y "current\"
/// se REEMPLAZA en cada update, por eso los logs van al padre (%LocalAppData%\PuduPrintServer\logs\).
/// En dev (dotnet run) queda en bin\...\logs\. Si no se puede escribir ahí, cae a
/// %USERPROFILE%\pudu-print-server\logs\ (misma carpeta que usa DatabaseService).
///
/// Sin librerías externas a propósito: el agente es self-contained y no necesita Serilog/NLog.
/// </summary>
public class LogService
{
    private static LogService? _i;
    public static LogService Instance => _i ??= new LogService();

    private const int DiasRetencion = 14;
    private const string Prefijo = "print-server-";

    private readonly object _lock = new();

    public string Carpeta { get; }

    /// <summary>Se dispara por cada línea escrita (ya formateada) — la ventana principal la usa
    /// para "Actividad reciente", así UI y archivo muestran exactamente lo mismo.</summary>
    public event Action<string>? Escrito;

    private LogService()
    {
        Carpeta = ResolverCarpeta();
        LimpiarAntiguos();
    }

    public void Info(string origen, string msg)  => Escribir("INFO ", origen, msg);
    public void Warn(string origen, string msg)  => Escribir("WARN ", origen, msg);

    public void Error(string origen, string msg, Exception? ex = null)
    {
        var texto = ex == null ? msg : $"{msg} — {ex.GetType().Name}: {ex.Message}";
        Escribir("ERROR", origen, texto);
        if (ex?.StackTrace != null)
            EscribirRaw("    " + ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "    "));
    }

    private void Escribir(string nivel, string origen, string msg)
    {
        var linea = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{nivel}]  [{origen,-6}]  {msg}";
        EscribirRaw(linea);
        // La UI muestra hora corta + origen + mensaje (mismo formato que tenía antes)
        try { Escrito?.Invoke($"{DateTime.Now:HH:mm}  {origen,-12}  {msg}"); } catch { /* UI cerrada */ }
    }

    private void EscribirRaw(string linea)
    {
        var archivo = Path.Combine(Carpeta, $"{Prefijo}{DateTime.Now:yyyy-MM-dd}.txt");
        lock (_lock)
        {
            try { File.AppendAllText(archivo, linea + Environment.NewLine, Encoding.UTF8); }
            catch { /* disco lleno / sin permisos: no botar el agente por un log */ }
        }
    }

    private static string ResolverCarpeta()
    {
        var candidatas = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "pudu-print-server", "logs"),
        };
        foreach (var dir in candidatas)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var prueba = Path.Combine(dir, ".write-test");
                File.WriteAllText(prueba, "");
                File.Delete(prueba);
                return dir;
            }
            catch { /* probar la siguiente */ }
        }
        return candidatas[^1];
    }

    private void LimpiarAntiguos()
    {
        try
        {
            var limite = DateTime.Now.AddDays(-DiasRetencion);
            foreach (var f in Directory.GetFiles(Carpeta, $"{Prefijo}*.txt"))
                if (File.GetLastWriteTime(f) < limite) File.Delete(f);
        }
        catch { /* best-effort */ }
    }
}
