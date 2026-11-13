using System.IO;
using System.Diagnostics;
using System.Text;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7: corre scripts de PowerShell (cmdlets PrintManagement: Add-PrinterDriver, Add-PrinterPort, Add-Printer…).
/// <see cref="EjecutarElevadoAsync"/> pide permiso de administrador (UAC) solo cuando el normal no alcanza.
/// </summary>
public static class PowerShellRunner
{
    public record Resultado(bool Ok, string Salida, string Error);

    public static async Task<Resultado> EjecutarAsync(string script, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Codificar(script)}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var salida = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return new(false, "", "Tiempo de espera agotado"); }
        return new(p.ExitCode == 0, (await salida).Trim(), (await error).Trim());
    }

    /// <summary>
    /// Igual, pero con UAC. La salida no se puede redirigir en un proceso elevado: el script escribe su resultado
    /// en un archivo temporal que se lee al terminar.
    /// </summary>
    public static async Task<Resultado> EjecutarElevadoAsync(string script, int timeoutMs = 120000)
    {
        var archivo = Path.Combine(Path.GetTempPath(), $"pudu-ps-{Guid.NewGuid():N}.txt");
        var envuelto = $$"""
            try { $r = & { {{script}} } 2>&1 | Out-String; Set-Content -LiteralPath '{{archivo}}' -Value ("OK`n" + $r) -Encoding UTF8 }
            catch { Set-Content -LiteralPath '{{archivo}}' -Value ("ERROR`n" + $_.Exception.Message) -Encoding UTF8 }
            """;
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {Codificar(envuelto)}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(timeoutMs);
            await p.WaitForExitAsync(cts.Token);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, "", "Se canceló el permiso de administrador");
        }
        catch (OperationCanceledException)
        {
            return new(false, "", "Tiempo de espera agotado");
        }
        try
        {
            if (!File.Exists(archivo)) return new(false, "", "El script elevado no dejó resultado");
            var texto = (await File.ReadAllTextAsync(archivo)).Trim();
            var ok = texto.StartsWith("OK", StringComparison.Ordinal);
            var cuerpo = texto.Length > 3 ? texto[3..].Trim() : "";
            return new(ok, ok ? cuerpo : "", ok ? "" : cuerpo);
        }
        finally { try { File.Delete(archivo); } catch { } }
    }

    private static string Codificar(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
