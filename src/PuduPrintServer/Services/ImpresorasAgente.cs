using System.Text.Json;
using Microsoft.Win32;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): qué camino usa cada impresora de Windows.
///  - Térmica de RED agregada por el asistente → bytes directo por TCP a su IP actual (la IP se corrige sola por
///    MAC, ver <see cref="RedReconexion"/>); no depende del puerto de Windows ni de permisos de administrador.
///  - Cola sin driver gráfico ("Generic / Text Only", o marcada raster por el asistente) → ESC/POS RAW por el spooler.
///  - El resto (driver del fabricante) → como hasta la 1.0.6 (GDI para el PDF, RAW para corte y cajón).
/// Todo vive en la tabla config (SQLite): `red:{nombre}` = JSON, `ticket_raster:{nombre}` = "1",
/// `ancho_puntos:{nombre}` = 576 | 384.
/// </summary>
public static class ImpresorasAgente
{
    public const string DriverGenerico = "Generic / Text Only";

    public record ImpresoraRed(string Ip, int Puerto, string Mac);

    private static DatabaseService Db => DatabaseService.Instance;

    public static ImpresoraRed? Red(string nombre)
    {
        var json = Db.GetConfig($"red:{nombre}");
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ImpresoraRed>(json); } catch { return null; }
    }

    public static void GuardarRed(string nombre, ImpresoraRed red)
    {
        Db.SetConfig($"red:{nombre}", JsonSerializer.Serialize(red));
        var nombres = NombresRed();
        if (!nombres.Contains(nombre, StringComparer.OrdinalIgnoreCase))
        {
            nombres.Add(nombre);
            Db.SetConfig("impresoras_red", JsonSerializer.Serialize(nombres));
        }
    }

    public static List<string> NombresRed()
    {
        try { return JsonSerializer.Deserialize<List<string>>(Db.GetConfig("impresoras_red") ?? "[]") ?? []; }
        catch { return []; }
    }

    public static void MarcarRaster(string nombre, int anchoPuntos)
    {
        Db.SetConfig($"ticket_raster:{nombre}", "1");
        Db.SetConfig($"ancho_puntos:{nombre}", anchoPuntos.ToString());
    }

    public static int AnchoPuntos(string nombre)
        => int.TryParse(Db.GetConfig($"ancho_puntos:{nombre}"), out var a) && a >= 200 ? a : 576;

    /// <summary>El ticket PDF va como imagen ESC/POS (sin driver) en vez de por GDI.</summary>
    public static bool UsaRaster(string nombre)
        => Red(nombre) is not null
        || Db.GetConfig($"ticket_raster:{nombre}") == "1"
        || string.Equals(DriverDe(nombre), DriverGenerico, StringComparison.OrdinalIgnoreCase);

    /// <summary>Driver de una cola local (registro del spooler); null si no se puede leer.</summary>
    public static string? DriverDe(string nombre)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Print\Printers\{nombre}");
            return k?.GetValue("Printer Driver") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Bytes ESC/POS a la impresora por el camino que le corresponde: red → TCP directo (corrigiendo la IP por MAC
    /// si cambió), si no → RAW por el spooler.
    /// </summary>
    public static async Task EnviarAsync(string nombre, byte[] bytes)
    {
        var red = Red(nombre);
        if (red is null)
        {
            await Task.Run(() => RawPrinterHelper.SendBytes(nombre, bytes));
            return;
        }
        var actual = await RedReconexion.AsegurarAsync(nombre, red);
        await new PrinterService().PrintIpAsync(bytes, actual.Ip, actual.Puerto);
    }
}
