namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (pedido de Jean: "si apagas y prendes la red cambia la IP… habría que poner forma de reconectarlo"):
/// cada térmica de red se guarda con su MAC. Antes de imprimir y cada 60 s se prueba IP:9100; si no responde se
/// busca la MAC en la red, se guarda la IP nueva y se intenta actualizar el puerto de Windows (sin pedir
/// administrador: si Windows no deja, el agente igual imprime directo a la IP nueva).
/// </summary>
public static class RedReconexion
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static Timer? _timer;
    /// <summary>Code review: una impresora simplemente apagada reescaneaba la red en cada tick y en cada impresión.
    /// Como mucho un escaneo cada 2 minutos por impresora.</summary>
    private static readonly Dictionary<string, DateTime> _ultimoEscaneo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan EntreEscaneos = TimeSpan.FromMinutes(2);

    public static void Iniciar()
    {
        _timer ??= new Timer(async _ =>
        {
            foreach (var nombre in ImpresorasAgente.NombresRed())
            {
                var red = ImpresorasAgente.Red(nombre);
                if (red is null) continue;
                try { await AsegurarAsync(nombre, red); }
                catch (Exception ex) { LogService.Instance.Warn("red", $"'{nombre}': {ex.Message}"); }
            }
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    /// <summary>Devuelve la dirección que responde (la guardada o la nueva encontrada por MAC).</summary>
    public static async Task<ImpresorasAgente.ImpresoraRed> AsegurarAsync(string nombre, ImpresorasAgente.ImpresoraRed red)
    {
        if (await DescubrirImpresoras.PuertoAbiertoAsync(red.Ip, red.Puerto, 800)) return red;

        await _lock.WaitAsync();
        try
        {
            // Otro hilo pudo haberla corregido mientras se esperaba el lock.
            var guardada = ImpresorasAgente.Red(nombre) ?? red;
            if (guardada.Ip != red.Ip && await DescubrirImpresoras.PuertoAbiertoAsync(guardada.Ip, guardada.Puerto, 800))
                return guardada;

            if (string.IsNullOrEmpty(guardada.Mac))
                throw new Exception($"No responde {guardada.Ip}:{guardada.Puerto} y no tiene MAC guardada para buscarla");
            if (_ultimoEscaneo.TryGetValue(nombre, out var ultimo) && DateTime.UtcNow - ultimo < EntreEscaneos)
                throw new Exception($"'{nombre}' no responde en {guardada.Ip} (apagada o desconectada)");
            _ultimoEscaneo[nombre] = DateTime.UtcNow;

            LogService.Instance.Warn("red", $"'{nombre}' no responde en {guardada.Ip}; buscando su MAC {guardada.Mac}…");
            var nuevaIp = await DescubrirImpresoras.BuscarIpPorMacAsync(guardada.Mac);
            if (nuevaIp is null)
                throw new Exception($"'{nombre}' no aparece en la red (MAC {guardada.Mac}). ¿Está encendida?");

            var nueva = guardada with { Ip = nuevaIp };
            ImpresorasAgente.GuardarRed(nombre, nueva);
            LogService.Instance.Info("red", $"'{nombre}' cambió de IP: {guardada.Ip} → {nuevaIp}");
            _ = ActualizarPuertoWindowsAsync(nombre, nueva);
            return nueva;
        }
        finally { _lock.Release(); }
    }

    private static async Task ActualizarPuertoWindowsAsync(string nombre, ImpresorasAgente.ImpresoraRed red)
    {
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var puerto = $"IP_{red.Ip}";
        var script = $"""
            $ErrorActionPreference = 'Stop'
            if (-not (Get-PrinterPort -Name {Q(puerto)} -ErrorAction SilentlyContinue)) {"{"} Add-PrinterPort -Name {Q(puerto)} -PrinterHostAddress {Q(red.Ip)} -PortNumber {red.Puerto} {"}"}
            Set-Printer -Name {Q(nombre)} -PortName {Q(puerto)}
            'OK'
            """;
        var r = await PowerShellRunner.EjecutarAsync(script);
        if (r.Ok) LogService.Instance.Info("red", $"Puerto de Windows de '{nombre}' actualizado a {puerto}");
        else LogService.Instance.Warn("red", $"No se pudo actualizar el puerto de Windows de '{nombre}' (el agente imprime igual a {red.Ip}): {r.Error}");
    }
}
