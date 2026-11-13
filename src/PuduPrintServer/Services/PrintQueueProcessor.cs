using System.Net;
using System.Net.Http;
using System.Timers;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public record PrintJobResult(ColaImpresionJob Job, bool Ok, string? Error = null, string Origen = "cola");

public class PrintQueueProcessor
{
    private static PrintQueueProcessor? _i;
    public static PrintQueueProcessor Instance => _i ??= new PrintQueueProcessor();

    private const int IntervaloOnlineMs  = 1500;
    private const int IntervaloOfflineMs = 5000; // backoff suave mientras no hay red

    private readonly SupabaseService  _supa      = SupabaseService.Instance;
    private readonly PrinterService   _printer   = new();
    private readonly PrintFormatter   _formatter = new();
    private readonly DatabaseService  _db        = DatabaseService.Instance;
    private readonly LogService       _log       = LogService.Instance;
    private System.Timers.Timer?      _timer;
    private volatile bool             _running;
    private DateTime                  _ultimoRefresh = DateTime.MinValue; // throttle del refresh ante 401
    private static readonly TimeSpan  RefreshCooldown = TimeSpan.FromSeconds(60);

    // Serializa TODA la impresión de comandas (poll + /comanda concurrentes): un COM abierto dos
    // veces lanza "port in use" y dos conexiones TCP 9100 a la misma térmica se pisan.
    private readonly SemaphoreSlim    _printLock = new(1, 1);

    // Ids ya impresos con éxito (últimos 200), sin importar el origen (poll o /comanda). Es lo
    // que evita la doble impresión cuando el POS, tras un timeout del endpoint local, encola la
    // MISMA comanda (mismo id) en cola_impresion: si acá ya salió, el poll la marca impresa sin
    // volver a imprimir. LocalHttpServer consulta este mismo registro para sus reintentos.
    private readonly object          _impresasLock  = new();
    private readonly Queue<string>   _impresasOrden = new();
    private readonly HashSet<string> _impresas      = new();
    private const int MaxImpresas = 200;

    public bool YaImpresa(string id)
    {
        lock (_impresasLock) return _impresas.Contains(id);
    }

    private void RegistrarImpresa(string id)
    {
        lock (_impresasLock)
        {
            if (!_impresas.Add(id)) return;
            _impresasOrden.Enqueue(id);
            while (_impresasOrden.Count > MaxImpresas)
                _impresas.Remove(_impresasOrden.Dequeue());
        }
    }

    public event Action<PrintJobResult>? JobCompleted;
    public event Action<string>? JobReceiving; // zona_id — se dispara antes de imprimir

    /// <summary>¿El último poll a Supabase salió bien? Al arrancar se asume true hasta que falle.</summary>
    public bool Online { get; private set; } = true;
    public event Action<bool>? OnlineChanged;

    public void Start(int intervalMs = IntervaloOnlineMs)
    {
        _ultimoRefresh = DateTime.MinValue; // tras re-login, el primer 401 puede refrescar de inmediato
        _timer?.Dispose();
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += OnTick;
        _timer.Start();
        _log.Info("cola", "Poll de cola_impresion iniciado");
    }

    public void Stop() => _timer?.Stop();

    private async void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_running) return;
        _running = true;
        try
        {
            var jobs = await _supa.GetJobsPendientesAsync();
            SetOnline(true);
            foreach (var job in jobs)
                await ProcessJobAsync(job);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Volvió la red pero el token guardado ya no sirve: reintentar el refresh, como máximo
            // una vez por minuto (si el refresh mismo falla por red/429, se vuelve a intentar
            // después). Sin logout automático: un 401 transitorio no debe botar al login.
            if (DateTime.Now - _ultimoRefresh >= RefreshCooldown)
            {
                _ultimoRefresh = DateTime.Now;
                _log.Warn("cola", "Supabase respondió 401: reintentando refresh de sesión");
                try { await new AuthService().TryRestoreSessionAsync(); }
                catch (Exception rex) { _log.Error("cola", "Refresh de sesión falló", rex); }
            }
            else
                SetOnline(false, "Sesión inválida en Supabase (401): cierra sesión y vuelve a ingresar");
        }
        catch (Exception ex)
        {
            // Sin red / DNS / timeout: NO botar el agente. Marcar offline y seguir intentando.
            SetOnline(false, $"Sin conexión a Supabase — {ex.GetType().Name}: {ex.Message}");
        }
        finally { _running = false; }
    }

    private void SetOnline(bool online, string? motivo = null)
    {
        if (online) _ultimoRefresh = DateTime.MinValue;
        if (Online == online) return; // solo loguear/notificar cuando CAMBIA (no una línea por tick)
        Online = online;
        if (_timer != null) _timer.Interval = online ? IntervaloOnlineMs : IntervaloOfflineMs;
        if (online) _log.Info("cola", "Conexión a Supabase recuperada");
        else        _log.Error("cola", motivo ?? "Sin conexión a Supabase");
        OnlineChanged?.Invoke(online);
    }

    private async Task ProcessJobAsync(ColaImpresionJob job)
    {
        if (YaImpresa(job.Id))
        {
            // El POS la mandó por /comanda, el agente la imprimió pero la respuesta no llegó a
            // tiempo, y el POS la encoló con el mismo id como respaldo. Ya salió: solo cerrarla.
            _log.Warn("cola", $"Comanda {job.Id} ya impresa por el endpoint local: se marca impresa sin reimprimir");
            await _supa.PatchEstadoAsync(job.Id, "impreso");
            return;
        }
        await _supa.PatchEstadoAsync(job.Id, "procesando");
        try
        {
            await ImprimirComandaAsync(job, "cola");
            await _supa.PatchEstadoAsync(job.Id, "impreso");
        }
        catch (Exception ex)
        {
            // Si tampoco hay red para reportar el error, loguear y seguir: el job queda
            // "procesando" en la BD (comportamiento previo, no se reintenta en esta fase).
            try { await _supa.PatchEstadoAsync(job.Id, "error", ex.Message[..Math.Min(ex.Message.Length, 200)]); }
            catch (Exception pex) { _log.Error("cola", $"No se pudo marcar error del job {job.Id}", pex); }
        }
    }

    /// <summary>
    /// Formatea e imprime una comanda resolviendo la impresora (IP del job → impresora local por
    /// zona en SQLite → spooler/COM). NO toca Supabase: la usan tanto el poll de la cola como el
    /// endpoint local POST /comanda. Lanza si falla; siempre dispara JobReceiving/JobCompleted.
    /// </summary>
    public async Task ImprimirComandaAsync(ColaImpresionJob job, string origen)
    {
        JobReceiving?.Invoke(job.ZonaId);
        var zona = job.ZonaNombre ?? job.ZonaId[..Math.Min(8, job.ZonaId.Length)];
        var mesa = job.MesaNumero ?? "-";
        await _printLock.WaitAsync();
        try
        {
            // Re-chequeo DENTRO del lock: la misma comanda puede llegar por /comanda y por la cola
            // (mismo id) y quedar las dos esperando el lock; la segunda no debe volver a imprimir.
            if (YaImpresa(job.Id))
            {
                _log.Warn(origen, $"Comanda {zona,-12} Mesa {mesa,-6} ya impresa (id {job.Id}): omitida");
                JobCompleted?.Invoke(new PrintJobResult(job, true, null, origen));
                return;
            }

            // 1.0.6: ancho de papel configurable (config.ancho_columnas): 48 = 80 mm (default), 32 = 58 mm.
            // Acá solo se parsea (no numérico → 48); el rango 24–64 (fuera → 48) se valida en un
            // único lugar: PrintFormatter.FormatComanda. Se quitó la lectura de negocio_nombre
            // (el diseño nuevo no lo imprime).
            var ancho = int.TryParse(_db.GetConfig("ancho_columnas"), out var a) ? a : PrintFormatter.AnchoPorDefecto;
            var bytes = _formatter.FormatComanda(job, ancho);

            // Resolver impresora:
            // 1. impresora_ip ya viene en el job (copiado por POS Matic desde zonas_produccion)
            // 2. Si no hay IP → buscar impresora local USB/BT por zona_id en SQLite
            if (!string.IsNullOrEmpty(job.ImpresoraIp))
            {
                await _printer.PrintIpAsync(bytes, job.ImpresoraIp, job.ImpresoraPuerto ?? 9100);
            }
            else
            {
                var imp = _db.GetImpresoras()
                             .FirstOrDefault(i => i.ZonaId == job.ZonaId && i.Activa);
                if (imp == null)
                {
                    // 1.0.7 (pedido de Jean): prioridad a la impresora de la zona; si la zona no tiene, la comanda sale
                    // por la impresora de caja del agente (la configurada aquí o la última que mandó POS Matic) en vez
                    // de fallar. Un local con una sola térmica no tiene que configurar zonas.
                    var caja = _db.GetConfig("impresora_ticket");
                    if (string.IsNullOrWhiteSpace(caja)) caja = _db.GetConfig("ultima_impresora_ticket");
                    if (string.IsNullOrWhiteSpace(caja))
                        throw new Exception($"Sin impresora configurada para zona {zona} (ni impresora de caja en el agente)");
                    _log.Warn(origen, $"Zona {zona} sin impresora: la comanda sale por la de caja '{caja}'");
                    await ImpresorasAgente.EnviarAsync(caja, bytes);
                }
                else if (imp.TipoConexion == "WIN")
                    // 1.0.7: una térmica de red del asistente va por TCP directo (con reconexión por MAC).
                    await ImpresorasAgente.EnviarAsync(imp.NombreImpresora, bytes);
                else
                    await _printer.PrintComAsync(bytes, imp.PuertoCom);
            }

            RegistrarImpresa(job.Id);
            _log.Info(origen, $"Comanda {zona,-12} Mesa {mesa,-6} ✓ impreso");
            JobCompleted?.Invoke(new PrintJobResult(job, true, null, origen));
        }
        catch (Exception ex)
        {
            _log.Error(origen, $"Comanda {zona,-12} Mesa {mesa,-6} ✗ {ex.Message}");
            JobCompleted?.Invoke(new PrintJobResult(job, false, ex.Message, origen));
            throw;
        }
        finally { _printLock.Release(); }
    }
}
