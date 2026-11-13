using System.Windows;
using Velopack;
using Velopack.Sources;

namespace PuduPrintServer.Services;

/// <summary>
/// Actualizaciones desde los releases de GitHub (Velopack).
/// 1.0.7 (Jean: "necesito que se actualice solo, que no tenga que estar bajándolo de la página"):
///  - revisa al arrancar y cada 4 h (antes solo al arrancar: un agente abierto días nunca se enteraba);
///  - descarga en segundo plano y la aplica SOLA cuando lleva 2 min sin imprimir (nunca a mitad de un ticket o
///    una comanda), reiniciando en la bandeja si la ventana estaba cerrada;
///  - el botón "Actualizar" de la ventana sigue para hacerlo al tiro.
/// La actividad la marcan LocalHttpServer (/imprimir, /cajon, /comanda) y PrintQueueProcessor (cola).
/// </summary>
public class UpdateService
{
    private static UpdateService? _i;
    public static UpdateService Instance => _i ??= new UpdateService();

    private const string RepoUrl = "https://github.com/foxen2005/pudu-print-server";
    private static readonly TimeSpan CadaCuanto = TimeSpan.FromHours(4);
    private static readonly TimeSpan Inactividad = TimeSpan.FromMinutes(2);

    private UpdateManager? _mgr;
    private UpdateInfo?    _updateListo;
    private Timer?         _revision;
    private Timer?         _aplicar;
    private int            _revisando;

    // ── Actividad de impresión ──────────────────────────────────────────────
    private static long _ultimaActividadTicks = DateTime.UtcNow.Ticks;
    private static int  _enCurso;

    /// <summary>Marca un trabajo en curso; al hacer Dispose queda registrado como terminado.</summary>
    public static IDisposable Trabajo()
    {
        Interlocked.Increment(ref _enCurso);
        Interlocked.Exchange(ref _ultimaActividadTicks, DateTime.UtcNow.Ticks);
        return new Fin();
    }

    private sealed class Fin : IDisposable
    {
        private int _hecho;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _hecho, 1) == 1) return;
            Interlocked.Decrement(ref _enCurso);
            Interlocked.Exchange(ref _ultimaActividadTicks, DateTime.UtcNow.Ticks);
        }
    }

    private static bool Inactivo =>
        Volatile.Read(ref _enCurso) == 0
        && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _ultimaActividadTicks), DateTimeKind.Utc) > Inactividad;

    public string? VersionLista => _updateListo?.TargetFullRelease.Version.ToString();
    public event Action<string>? UpdateReady;

    private UpdateService() { }

    /// <summary>Primera revisión (al arrancar) y programa las siguientes cada 4 h.</summary>
    public async Task CheckAndDownloadAsync()
    {
        _revision ??= new Timer(async _ => await RevisarAsync(), null, CadaCuanto, CadaCuanto);
        await RevisarAsync();
    }

    private async Task RevisarAsync()
    {
        if (_updateListo != null || Interlocked.Exchange(ref _revisando, 1) == 1) return;
        try
        {
            _mgr ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!_mgr.IsInstalled) return;
            var nueva = await _mgr.CheckForUpdatesAsync();
            if (nueva == null) return;
            await _mgr.DownloadUpdatesAsync(nueva);
            _updateListo = nueva;
            var version = nueva.TargetFullRelease.Version.ToString();
            LogService.Instance.Info("update", $"Versión {version} descargada; se instala sola cuando no esté imprimiendo");
            UpdateReady?.Invoke(version);
            _aplicar ??= new Timer(_ => AplicarSiInactivo(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        catch { /* sin red o sin release — silencioso, se reintenta en 4 h */ }
        finally { Interlocked.Exchange(ref _revisando, 0); }
    }

    private void AplicarSiInactivo()
    {
        if (_mgr == null || _updateListo == null || !Inactivo) return;
        _aplicar?.Dispose();
        LogService.Instance.Info("update", $"Instalando la versión {VersionLista} (2 min sin imprimir) y reiniciando");
        ApplyAndRestart();
    }

    public void ApplyAndRestart()
    {
        if (_mgr == null || _updateListo == null) return;
        // Si la ventana estaba cerrada (agente en la bandeja), vuelve a la bandeja sin abrirla.
        bool ventanaVisible = false;
        try
        {
            ventanaVisible = Application.Current?.Dispatcher.Invoke(() =>
                Application.Current.Windows.OfType<Views.MainWindow>().Any(w => w.IsVisible)) ?? false;
        }
        catch { /* sin dispatcher: se trata como oculta */ }
        _mgr.ApplyUpdatesAndRestart(_updateListo.TargetFullRelease,
            ventanaVisible ? [] : [AutoArranque.ArgOculto]);
    }
}
