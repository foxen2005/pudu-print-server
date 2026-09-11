using System.IO;
using System.Net;
using System.Printing;
using System.Text;
using System.Text.Json;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

/// <summary>
/// Servidor HTTP local (loopback) que recibe tickets de caja del POS y los imprime.
///
/// El POS (pudusystem.com, HTTPS) corre en la MISMA PC y hace:
///   GET  http://127.0.0.1:{puerto}/health      → ¿agente vivo?
///   GET  http://127.0.0.1:{puerto}/impresoras  → lista de impresoras de Windows
///   POST http://127.0.0.1:{puerto}/imprimir    → { pdfBase64, impresora? } → imprime y responde ok/error
///   POST http://127.0.0.1:{puerto}/cajon       → abre el cajón monedero
///   POST http://127.0.0.1:{puerto}/comanda     → comanda a cocina directa (sin pasar por cola_impresion)
///
/// Binding a 127.0.0.1 (no "+"/"*") NO requiere permisos de admin ni urlacl.
/// No depende del login de Supabase ni de internet: todo viaja en el request.
/// </summary>
public class LocalHttpServer
{
    public const int DefaultPort = 21777;

    private readonly HttpListener      _listener = new();
    private readonly PdfPrinterService _pdf      = new();
    private readonly DatabaseService   _db       = DatabaseService.Instance;
    private readonly LogService        _log      = LogService.Instance;
    private CancellationTokenSource?   _cts;

    // Idempotencia de /comanda: el POS reintenta con el mismo id si hubo timeout → no reimprimir.
    private readonly Queue<string>   _comandasVistasOrden = new();
    private readonly HashSet<string> _comandasVistas      = new();
    private const int MaxComandasVistas = 200;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public int  Port      { get; private set; }
    public bool IsRunning { get; private set; }

    public void Start()
    {
        Port = int.TryParse(_db.GetConfig("http_port"), out var p) && p is > 0 and < 65536
            ? p : DefaultPort;

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        _log.Info("local", $"Servidor local escuchando en http://127.0.0.1:{Port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ya detenido */ }
        IsRunning = false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                // Detenido explícitamente → salir. Error transitorio → seguir escuchando.
                if (ct.IsCancellationRequested || !_listener.IsListening) break;
                continue;
            }
            _ = HandleAsync(ctx); // un request no bloquea a los siguientes
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        AddCors(req, res);

        try
        {
            // Preflight CORS (Chrome Private Network Access — página HTTPS → 127.0.0.1)
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            var path = req.Url?.AbsolutePath ?? "/";

            // /health es inofensivo y sirve de detección → abierto.
            if (req.HttpMethod == "GET" && path == "/health")
            {
                await WriteJson(res, 200, new { ok = true, servicio = "pudu-print-server", puerto = Port });
                return;
            }

            // Endpoints con efecto (imprimir) o que filtran datos (impresoras): solo orígenes conocidos.
            // El navegador controla el header Origin, no lo puede falsear una web maliciosa.
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !OrigenPermitido(origin))
            {
                await WriteJson(res, 403, new { ok = false, error = "origen no autorizado" });
                return;
            }

            if (req.HttpMethod == "GET" && path == "/impresoras")
            {
                await WriteJson(res, 200, new { ok = true, impresoras = ListarImpresoras() });
                return;
            }

            if (req.HttpMethod == "POST" && path == "/imprimir")
            {
                await HandleImprimirAsync(req, res);
                return;
            }

            if (req.HttpMethod == "POST" && path == "/cajon")
            {
                await HandleCajonAsync(req, res);
                return;
            }

            if (req.HttpMethod == "POST" && path == "/comanda")
            {
                await HandleComandaAsync(req, res);
                return;
            }

            await WriteJson(res, 404, new { ok = false, error = "ruta no encontrada" });
        }
        catch (Exception ex)
        {
            _log.Error("local", $"Error en request {req.HttpMethod} {req.Url?.AbsolutePath}", ex);
            try { await WriteJson(res, 500, new { ok = false, error = ex.Message }); } catch { /* conexión cerrada */ }
        }
    }

    private const long MaxBodyBytes = 20 * 1024 * 1024; // 20 MB — un ticket PDF pesa KB, esto es tope de sanidad

    // Comando ESC/POS de apertura de cajón monedero (pulso pin 2, t1=25ms t2=250ms).
    private static readonly byte[] CashDrawerCmd = { 0x1B, 0x70, 0x00, 0x19, 0xFA };

    private async Task HandleImprimirAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (req.ContentLength64 > MaxBodyBytes)
        { await WriteJson(res, 413, new { ok = false, error = "cuerpo demasiado grande" }); return; }

        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        ImprimirRequest? payload;
        try { payload = JsonSerializer.Deserialize<ImprimirRequest>(body, JsonOpts); }
        catch { await WriteJson(res, 400, new { ok = false, error = "JSON inválido" }); return; }

        if (payload is null || string.IsNullOrEmpty(payload.PdfBase64))
        { await WriteJson(res, 400, new { ok = false, error = "pdfBase64 requerido" }); return; }

        // La impresora la elige el POS (dropdown en Settings); si no viene, cae a la config local.
        var impresora = !string.IsNullOrWhiteSpace(payload.Impresora)
            ? payload.Impresora!
            : _db.GetConfig("impresora_ticket");
        if (string.IsNullOrWhiteSpace(impresora))
        { await WriteJson(res, 400, new { ok = false, error = "Sin impresora de ticket (envía 'impresora' o configúrala en el agente)" }); return; }

        // Validar que la impresora exista de antemano → un nombre viejo/erróneo da error detectable.
        if (!ImpresoraExiste(impresora))
        { await WriteJson(res, 400, new { ok = false, error = $"Impresora no encontrada en Windows: {impresora}" }); return; }

        byte[] pdf;
        try { pdf = Convert.FromBase64String(payload.PdfBase64); }
        catch { await WriteJson(res, 400, new { ok = false, error = "pdfBase64 no es base64 válido" }); return; }

        await _pdf.PrintPdfAsync(pdf, impresora);

        // Cajón monedero: imprimir el PDF no envía el pulso de apertura, así que lo mandamos
        // aparte por RAW a la misma impresora (best-effort, no falla la venta).
        if (payload.AbrirCajon)
        {
            try { RawPrinterHelper.SendBytes(impresora, CashDrawerCmd); }
            catch (Exception ex) { _log.Error("local", "No se pudo abrir el cajón", ex); }
        }

        _log.Info("local", $"Ticket impreso en '{impresora}' ({pdf.Length} bytes)");
        await WriteJson(res, 200, new { ok = true });
    }

    /// <summary>
    /// Comanda a cocina directa desde el POS (misma PC), sin pasar por cola_impresion en Supabase.
    /// Mismos campos que la fila de la cola → reusa ColaImpresionJob + PrintFormatter + la
    /// resolución de impresora del poll (PrintQueueProcessor.ImprimirComandaAsync).
    /// </summary>
    private async Task HandleComandaAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (req.ContentLength64 > MaxBodyBytes)
        { await WriteJson(res, 413, new { ok = false, error = "cuerpo demasiado grande" }); return; }

        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        ComandaRequest? payload;
        try { payload = JsonSerializer.Deserialize<ComandaRequest>(body, JsonOpts); }
        catch { await WriteJson(res, 400, new { ok = false, error = "JSON inválido" }); return; }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ZonaId))
        { await WriteJson(res, 400, new { ok = false, error = "zonaId requerido" }); return; }
        if (payload.Items is not { ValueKind: JsonValueKind.Array } || payload.Items.Value.GetArrayLength() == 0)
        { await WriteJson(res, 400, new { ok = false, error = "items requerido (arreglo no vacío)" }); return; }

        // Reintento del POS con el mismo id → ya se imprimió, responder ok sin duplicar.
        if (!string.IsNullOrEmpty(payload.Id) && !RegistrarComanda(payload.Id))
        {
            _log.Warn("local", $"Comanda {payload.Id} repetida, no se reimprime");
            await WriteJson(res, 200, new { ok = true, duplicada = true });
            return;
        }

        var job = new ColaImpresionJob
        {
            Id              = payload.Id ?? Guid.NewGuid().ToString(),
            ZonaId          = payload.ZonaId,
            ZonaNombre      = payload.ZonaNombre,
            MesaNumero      = payload.MesaNumero,
            GarzonNombre    = payload.GarzonNombre,
            PedidoId        = payload.PedidoId,
            Items           = payload.Items,
            ImpresoraIp     = payload.ImpresoraIp,
            ImpresoraPuerto = payload.ImpresoraPuerto,
            Estado          = "local",
        };

        try
        {
            await PrintQueueProcessor.Instance.ImprimirComandaAsync(job, "local");
            await WriteJson(res, 200, new { ok = true });
        }
        catch (Exception ex)
        {
            // Ya quedó en el log/UI vía ImprimirComandaAsync; el POS cae a cola_impresion.
            if (!string.IsNullOrEmpty(payload.Id)) OlvidarComanda(payload.Id); // permitir reintento real
            await WriteJson(res, 500, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>true si es nueva (se registra); false si ya se había visto.</summary>
    private bool RegistrarComanda(string id)
    {
        lock (_comandasVistas)
        {
            if (!_comandasVistas.Add(id)) return false;
            _comandasVistasOrden.Enqueue(id);
            while (_comandasVistasOrden.Count > MaxComandasVistas)
                _comandasVistas.Remove(_comandasVistasOrden.Dequeue());
            return true;
        }
    }

    private void OlvidarComanda(string id)
    {
        lock (_comandasVistas) _comandasVistas.Remove(id);
    }

    /// <summary>Abre el cajón monedero on-demand (botón manual en el POS) enviando el pulso RAW.</summary>
    private async Task HandleCajonAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        CajonRequest? payload;
        try { payload = JsonSerializer.Deserialize<CajonRequest>(body, JsonOpts); }
        catch { payload = null; } // body vacío u opcional → usa la config local

        var impresora = !string.IsNullOrWhiteSpace(payload?.Impresora)
            ? payload!.Impresora!
            : _db.GetConfig("impresora_ticket");
        if (string.IsNullOrWhiteSpace(impresora))
        { await WriteJson(res, 400, new { ok = false, error = "Sin impresora (envía 'impresora' o configúrala en el agente)" }); return; }
        if (!ImpresoraExiste(impresora))
        { await WriteJson(res, 400, new { ok = false, error = $"Impresora no encontrada en Windows: {impresora}" }); return; }

        RawPrinterHelper.SendBytes(impresora, CashDrawerCmd);
        _log.Info("local", $"Cajón abierto en '{impresora}'");
        await WriteJson(res, 200, new { ok = true });
    }

    private static void AddCors(HttpListenerRequest req, HttpListenerResponse res)
    {
        // Reflejar SOLO orígenes conocidos (no "*"): si no, cualquier web abierta en el
        // navegador podría imprimir o enumerar impresoras vía este server local.
        var origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && OrigenPermitido(origin))
        {
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
        }
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        // Sin este header, Chrome bloquea la llamada de una página pública (HTTPS) a la red local (127.0.0.1)
        res.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    /// <summary>Dominios del POS autorizados a usar el agente (además de localhost para desarrollo).</summary>
    private static bool OrigenPermitido(string origin)
    {
        try
        {
            var host = new Uri(origin).Host;
            return host is "localhost" or "127.0.0.1"
                || host.EndsWith("pudusystem.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("pudutecnologia.cl", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<string> ListarImpresoras()
    {
        using var server = new LocalPrintServer();
        return server
            .GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
            .Select(q => q.FullName)
            .OrderBy(n => n)
            .ToList();
    }

    private static bool ImpresoraExiste(string nombre)
        => ListarImpresoras().Any(n => string.Equals(n, nombre, StringComparison.OrdinalIgnoreCase));

    private static async Task WriteJson(HttpListenerResponse res, int status, object body)
    {
        res.StatusCode  = status;
        res.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private class ImprimirRequest
    {
        public string? PdfBase64 { get; set; }
        public string? Impresora { get; set; }
        public bool    AbrirCajon { get; set; }
    }

    private class CajonRequest
    {
        public string? Impresora { get; set; }
    }

    private class ComandaRequest
    {
        public string?      Id              { get; set; } // uuid del POS, para idempotencia
        public string?      ZonaId          { get; set; }
        public string?      ZonaNombre      { get; set; }
        public string?      MesaNumero      { get; set; }
        public string?      GarzonNombre    { get; set; }
        public string?      PedidoId        { get; set; }
        public JsonElement? Items           { get; set; } // CartItem[] tal cual va a cola_impresion
        public string?      ImpresoraIp     { get; set; }
        public int?         ImpresoraPuerto { get; set; }
    }
}
