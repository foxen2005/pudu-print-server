using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): busca impresoras que Windows todavía no tiene.
///  - Red: puerto 9100 abierto en la subred local (/24 de cada adaptador activo) + su MAC (ARP), para reconocerla
///    aunque el router le cambie la IP.
///  - USB: puertos USB00x del spooler sin cola (Windows crea el puerto al enchufarla aunque no haya driver).
///  - Bluetooth / serie: puertos COM con su nombre ("Serie estándar sobre vínculo Bluetooth (COM5)").
/// </summary>
public static class DescubrirImpresoras
{
    public record EnRed(string Ip, string Mac);
    public record PuertoLocal(string Puerto, string Descripcion);

    private const int PuertoRaw = 9100;

    // ── Red ──────────────────────────────────────────────────────────────────

    /// <summary>Prefijos /24 ("192.168.1.") de los adaptadores activos con IPv4 privada (sin virtuales de Hyper-V/WSL).</summary>
    public static List<string> Subredes()
    {
        var lista = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                || ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                bool privada = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
                if (!privada) continue;
                var prefijo = $"{b[0]}.{b[1]}.{b[2]}.";
                if (!lista.Contains(prefijo)) lista.Add(prefijo);
            }
        }
        return lista;
    }

    /// <summary>Escanea las subredes buscando el puerto 9100. ~3-6 s por /24 (64 conexiones a la vez, 400 ms c/u).</summary>
    public static async Task<List<EnRed>> BuscarEnRedAsync(CancellationToken ct = default)
    {
        var ips = Subredes().SelectMany(p => Enumerable.Range(1, 254).Select(i => p + i)).ToList();
        var encontradas = new System.Collections.Concurrent.ConcurrentBag<EnRed>();
        using var sem = new SemaphoreSlim(64);
        await Task.WhenAll(ips.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                if (await PuertoAbiertoAsync(ip, PuertoRaw, 400))
                    encontradas.Add(new EnRed(ip, MacDe(ip)));
            }
            finally { sem.Release(); }
        }));
        return encontradas.OrderBy(e => IPAddress.Parse(e.Ip).GetAddressBytes()[3]).ToList();
    }

    /// <summary>Busca en la red la impresora con esta MAC (cuando su IP dejó de responder).</summary>
    public static async Task<string?> BuscarIpPorMacAsync(string mac, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var lista = await BuscarEnRedAsync(ct);
        return lista.FirstOrDefault(e => string.Equals(e.Mac, mac, StringComparison.OrdinalIgnoreCase))?.Ip;
    }

    public static async Task<bool> PuertoAbiertoAsync(string ip, int puerto, int timeoutMs)
    {
        // Con token (no Task.WhenAny): la conexión que vence se cancela y su excepción se observa acá. Antes quedaba
        // viva tras el Dispose y cada IP sin respuesta dejaba una "excepción no observada" en el log (254 por /24).
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, puerto, cts.Token);
            return tcp.Connected;
        }
        catch { return false; }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    /// <summary>MAC por ARP ("02-50-74-61-3F-64"); "" si no responde. Después de conectar al 9100 ya está en caché.</summary>
    public static string MacDe(string ip)
    {
        try
        {
            var mac = new byte[6];
            uint len = (uint)mac.Length;
            uint dest = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) != 0 || len == 0) return "";
            return string.Join("-", mac.Take((int)len).Select(x => x.ToString("X2")));
        }
        catch { return ""; }
    }

    // ── USB y Bluetooth/serie ───────────────────────────────────────────────

    /// <summary>Puertos USB00x del spooler que no usa ninguna cola (impresora enchufada sin instalar).</summary>
    public static async Task<List<PuertoLocal>> PuertosUsbLibresAsync()
    {
        const string script = """
            $usados = @(Get-Printer | ForEach-Object { $_.PortName })
            Get-PrinterPort | Where-Object { $_.Name -like 'USB*' -and $usados -notcontains $_.Name } |
              Select-Object Name, Description | ConvertTo-Json -Compress
            """;
        var r = await PowerShellRunner.EjecutarAsync(script);
        return ParsearPuertos(r.Salida, "Name", "Description");
    }

    /// <summary>Puertos COM con su nombre de dispositivo (Bluetooth SPP, USB-serie).</summary>
    public static async Task<List<PuertoLocal>> PuertosComAsync()
    {
        const string script = """
            Get-CimInstance Win32_PnPEntity -Filter "Name LIKE '%(COM%'" |
              Select-Object Name | ConvertTo-Json -Compress
            """;
        var r = await PowerShellRunner.EjecutarAsync(script);
        var nombres = ParsearPuertos(r.Salida, "Name", "Name");
        var lista = new List<PuertoLocal>();
        foreach (var com in SerialPort.GetPortNames().Distinct().OrderBy(p => p))
        {
            var desc = nombres.FirstOrDefault(n => n.Puerto.Contains($"({com})", StringComparison.OrdinalIgnoreCase))?.Puerto ?? com;
            lista.Add(new PuertoLocal(com, desc));
        }
        return lista;
    }

    private static List<PuertoLocal> ParsearPuertos(string json, string campoPuerto, string campoDesc)
    {
        var lista = new List<PuertoLocal>();
        if (string.IsNullOrWhiteSpace(json)) return lista;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var e in items)
            {
                var p = e.TryGetProperty(campoPuerto, out var v1) ? v1.GetString() ?? "" : "";
                var d = e.TryGetProperty(campoDesc, out var v2) ? v2.GetString() ?? "" : "";
                if (p != "") lista.Add(new PuertoLocal(p, d));
            }
        }
        catch { /* salida inesperada: lista vacía */ }
        return lista;
    }
}
