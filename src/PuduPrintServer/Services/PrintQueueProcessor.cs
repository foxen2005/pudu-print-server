using System.Timers;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public record PrintJobResult(ColaImpresionJob Job, bool Ok, string? Error = null);

public class PrintQueueProcessor
{
    private static PrintQueueProcessor? _i;
    public static PrintQueueProcessor Instance => _i ??= new PrintQueueProcessor();

    private readonly SupabaseService  _supa      = SupabaseService.Instance;
    private readonly PrinterService   _printer   = new();
    private readonly PrintFormatter   _formatter = new();
    private readonly DatabaseService  _db        = DatabaseService.Instance;
    private System.Timers.Timer?      _timer;
    private volatile bool             _running;

    public event Action<PrintJobResult>? JobCompleted;

    public void Start(int intervalMs = 1500)
    {
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += OnTick;
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private async void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_running) return;
        _running = true;
        try
        {
            var jobs = await _supa.GetJobsPendientesAsync();
            foreach (var job in jobs)
                await ProcessJobAsync(job);
        }
        finally { _running = false; }
    }

    private async Task ProcessJobAsync(ColaImpresionJob job)
    {
        // Marcar procesando (atómico: solo si sigue pendiente)
        await _supa.PatchEstadoAsync(job.Id, "procesando");
        try
        {
            var negocio = _db.GetConfig("negocio_nombre") ?? "PUDU";
            var bytes   = _formatter.FormatComanda(job, negocio);

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
                    throw new Exception($"Sin impresora configurada para zona {job.ZonaNombre ?? job.ZonaId}");
                await _printer.PrintComAsync(bytes, imp.PuertoCom);
            }

            await _supa.PatchEstadoAsync(job.Id, "impreso");
            JobCompleted?.Invoke(new PrintJobResult(job, true));
        }
        catch (Exception ex)
        {
            await _supa.PatchEstadoAsync(job.Id, "error", ex.Message[..Math.Min(ex.Message.Length, 200)]);
            JobCompleted?.Invoke(new PrintJobResult(job, false, ex.Message));
        }
    }
}
