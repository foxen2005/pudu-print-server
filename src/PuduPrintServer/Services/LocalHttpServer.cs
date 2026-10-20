using System.IO;
using System.Net;
using System.Printing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

/// <summary>
/// Servidor HTTP local (loopback) que recibe tickets de caja del POS y los imprime.
///
/// El POS (pudusystem.com, HTTPS) corre en la MISMA PC y hace:
///   GET  http://127.0.0.1:{puerto}/health      → ¿agente vivo? (1.0.6: incluye "version")
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
    // - _comandasEnCurso: id → Task de la impresión en curso. Un duplicado que llega mientras la
    //   primera aún imprime ESPERA ese mismo Task y responde con su resultado real (si falla,
    //   el POS ve el error y cae a cola_impresion; no un "ok" a ciegas).
    // - Ids ya impresos: registro compartido en PrintQueueProcessor (YaImpresa), el mismo que
    //   consulta el poll de cola_impresion → una comanda impresa acá y luego encolada con el
    //   mismo id como respaldo no sale dos veces.
    private readonly Dictionary<string, Task> _comandasEnCurso = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public int  Port      { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Versión de la assembly ("1.0.6"), la misma que muestra la ventana. Se informa en /health (1.0.6).</summary>
    private static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "desconocida";

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
        // 1.0.6 (code review): una línea por arranque con el valor efectivo, para diagnosticar un
        // doble corte (driver que ya corta + este corte RAW) sin tener que abrir la SQLite.
        _log.Info("local", $"cortar_despues_pdf = {(CortarDespuesPdf() ? "true" : "false")} " +
                           "(corte RAW GS V 65 0 después de cada PDF de /imprimir)");
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
                // 1.0.6: + version, para que el POS sepa qué formato/funciones acepta el agente.
                await WriteJson(res, 200, new { ok = true, servicio = "pudu-print-server", puerto = Port, version = Version });
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

        // 1.0.6 (bug 10 del plan 2026-10-20): el PDF va por GDI y el corte quedaba a criterio del
        // driver (muchos no cortan, o cortan después de un largo de página fijo). Después del
        // trabajo GDI se manda un trabajo RAW aparte con GS V 65 0 (corte completo: avanza hasta la
        // cuchilla y corta; code review: antes GS V 66 0, que es parcial). El spooler los procesa
        // en orden, así que el corte sale después del ticket.
        // Se apaga con config `cortar_despues_pdf` = false/0 si el driver ya corta (doble corte).
        // Best-effort: el ticket ya salió, un corte fallido no falla la venta.
        if (CortarDespuesPdf())
        {
            try { RawPrinterHelper.SendBytes(impresora, EscPos.CutFeed()); }
            catch (Exception ex) { _log.Error("local", "No se pudo cortar el papel después del PDF", ex); }
        }

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

        var id = string.IsNullOrEmpty(payload.Id) ? Guid.NewGuid().ToString() : payload.Id;
        var job = new ColaImpresionJob
        {
            Id              = id,
            ZonaId          = payload.ZonaId,
            ZonaNombre      = payload.ZonaNombre,
            MesaNumero      = payload.MesaNumero,
            GarzonNombre    = payload.GarzonNombre,
            PedidoId        = payload.PedidoId,
            Comensales      = payload.Comensales,
            Notas           = payload.Notas,
            Items           = payload.Items,
            ImpresoraIp     = payload.ImpresoraIp,
            ImpresoraPuerto = payload.ImpresoraPuerto,
            Estado          = "local",
            // 1.0.6: rótulo ADICIONAL / REIMPRESIÓN / ANULAR (camelCase o snake_case).
            TipoComanda     = payload.TipoComanda ?? payload.TipoComandaSnake,
            Secuencia       = payload.Secuencia,
            ZonaBase        = payload.ZonaBase,
        };

        Task impresion;
        bool duplicada;
        lock (_comandasEnCurso)
        {
            if (PrintQueueProcessor.Instance.YaImpresa(id))
            {
                _log.Warn("local", $"Comanda {id} ya impresa, no se reimprime");
                impresion = Task.CompletedTask;
                duplicada = true;
            }
            else if (_comandasEnCurso.TryGetValue(id, out var enCurso))
            {
                _log.Warn("local", $"Comanda {id} repetida mientras se imprime: esperando la primera");
                impresion = enCurso;
                duplicada = true;
            }
            else
            {
                impresion = ImprimirYRegistrarAsync(job);
                _comandasEnCurso[id] = impresion;
                duplicada = false;
            }
        }

        try
        {
            await impresion;
            await WriteJson(res, 200, new { ok = true, duplicada });
        }
        catch (Exception ex)
        {
            // Ya quedó en el log/UI vía ImprimirComandaAsync; el POS cae a cola_impresion.
            await WriteJson(res, 500, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Imprime (ImprimirComandaAsync registra el id si sale bien); en todo caso lo saca de "en curso".</summary>
    private async Task ImprimirYRegistrarAsync(ColaImpresionJob job)
    {
        try
        {
            await PrintQueueProcessor.Instance.ImprimirComandaAsync(job, "local");
        }
        finally
        {
            lock (_comandasEnCurso) _comandasEnCurso.Remove(job.Id);
        }
    }

    /// <summary>
    /// config.cortar_despues_pdf (1.0.6): default true; "false", "0", "no" lo apagan.
    /// </summary>
    private bool CortarDespuesPdf()
    {
        var v = _db.GetConfig("cortar_despues_pdf")?.Trim().ToLowerInvariant();
        return v is not ("false" or "0" or "no");
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
        public int?         Comensales      { get; set; }
        public string?      Notas           { get; set; }
        public JsonElement? Items           { get; set; } // CartItem[] tal cual va a cola_impresion (1.0.6: cada ítem puede traer ya_enviado)
        public string?      ImpresoraIp     { get; set; }
        public int?         ImpresoraPuerto { get; set; }

        // 1.0.6 — rótulo de la comanda v2. El POS manda camelCase ("tipoComanda"); se acepta también
        // snake_case ("tipo_comanda"), igual que la fila de cola_impresion. Si no vienen (POS viejo),
        // PrintFormatter deduce el rótulo del sufijo de zonaNombre o imprime como comanda normal.
        public string?      TipoComanda     { get; set; } // comanda | adicional | reimpresion | anulacion | marcado
        public int?         Secuencia       { get; set; } // N° de envío POR ZONA → "+ ADICIONAL #n" (solo con tipo adicional)
        public string?      ZonaBase        { get; set; } // nombre limpio ("Cocina"); zonaNombre trae el rótulo viejo pegado
        [JsonPropertyName("tipo_comanda")]
        public string?      TipoComandaSnake { get; set; }
    }
}
