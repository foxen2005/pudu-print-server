namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): instala en Windows una térmica que encontró el
/// asistente, para que aparezca en POS Matic ("Impresora de caja") y en cualquier programa.
///  - Driver: "Generic / Text Only" (viene con Windows, no se descarga nada). El ticket igual sale con timbre y logo
///    porque el agente lo manda como imagen ESC/POS (RAW), no por el driver.
///  - Puerto: red → puerto TCP/IP estándar a IP:9100; USB → el USB00x que creó Windows; Bluetooth/serie → COMx:.
///  - Primero sin permisos especiales; si Windows lo niega, pide permiso de administrador (UAC) una vez.
/// Queda registrada en el agente: raster + ancho, y para red IP/puerto/MAC (reconexión por MAC).
/// </summary>
public static class InstaladorImpresoras
{
    public enum Tipo { Red, Usb, Com }

    public record Pedido(Tipo Tipo, string Nombre, string Destino, string Mac = "", int Puerto = 9100, int AnchoPuntos = 576);

    public static async Task<(bool Ok, string Mensaje)> InstalarAsync(Pedido p)
    {
        var nombre = p.Nombre.Trim();
        if (nombre == "") return (false, "Falta el nombre de la impresora");
        if (nombre.IndexOfAny(['\\', ',', '!']) >= 0) return (false, "El nombre no puede tener \\ , ni !");

        string puerto = p.Tipo switch
        {
            Tipo.Red => $"IP_{p.Destino}",
            Tipo.Com => p.Destino.EndsWith(':') ? p.Destino : p.Destino + ":",
            _ => p.Destino,
        };
        string Q(string s) => "'" + s.Replace("'", "''") + "'";

        // Driver: el POS-80 que viene con el agente (una vez, con UAC) para que la cola funcione en cualquier programa;
        // si no se puede, "Generic / Text Only". El ticket del agente sale como imagen ESC/POS con cualquiera de los dos.
        var driver = await DriverPos80.InstalarElevadoAsync() ? DriverPos80.Nombre : ImpresorasAgente.DriverGenerico;

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $drv = {{Q(driver)}}
            if (-not (Get-PrinterDriver -Name $drv -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $drv }
            $port = {{Q(puerto)}}
            {{(p.Tipo == Tipo.Red
                ? $"if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name $port -PrinterHostAddress {Q(p.Destino)} -PortNumber {p.Puerto} }}"
                : "")}}
            $name = {{Q(nombre)}}
            if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { Set-Printer -Name $name -PortName $port -DriverName $drv }
            else { Add-Printer -Name $name -DriverName $drv -PortName $port }
            'INSTALADA'
            """;

        var r = await PowerShellRunner.EjecutarAsync(script);
        if (!r.Ok || !r.Salida.Contains("INSTALADA"))
        {
            LogService.Instance.Warn("asistente", $"Instalación sin permisos falló ({r.Error}); se pide administrador");
            r = await PowerShellRunner.EjecutarElevadoAsync(script);
        }
        if (!r.Ok || !r.Salida.Contains("INSTALADA"))
            return (false, string.IsNullOrWhiteSpace(r.Error) ? "Windows no instaló la impresora" : r.Error);

        ImpresorasAgente.MarcarRaster(nombre, p.AnchoPuntos);
        if (p.Tipo == Tipo.Red)
            ImpresorasAgente.GuardarRed(nombre, new ImpresorasAgente.ImpresoraRed(p.Destino, p.Puerto, p.Mac));

        LogService.Instance.Info("asistente", $"Impresora '{nombre}' instalada en {puerto} (driver {driver}, ticket raster {p.AnchoPuntos} puntos)");
        return (true, $"'{nombre}' quedó instalada en Windows.");
    }

    /// <summary>Ticket de prueba con texto y un bloque raster (comprueba que imprime imágenes) y corte.</summary>
    public static byte[] TicketPrueba(string nombre, int anchoPuntos)
    {
        using var bmp = new System.Drawing.Bitmap(anchoPuntos, 48);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            for (int x = 0; x < anchoPuntos; x += 24) g.FillRectangle(System.Drawing.Brushes.Black, x, 0, 12, 48);
        }
        return EscPos.Concat(
            EscPos.Initialize(), EscPos.Center(), EscPos.BoldOn(),
            EscPos.Line("PUDU PRINT SERVER"), EscPos.BoldOff(),
            EscPos.Line("Prueba de impresora"),
            EscPos.Line(EscPos.Imprimible(nombre)),
            EscPos.Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            EscPos.Feed(1),
            EscPosRaster.Convertir(bmp),
            EscPos.Line("Si ves las barras, imprime imagenes."),
            EscPos.Feed(3), EscPos.Cut());
    }
}
