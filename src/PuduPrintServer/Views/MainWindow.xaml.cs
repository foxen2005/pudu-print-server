using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PuduPrintServer.Controls;
using PuduPrintServer.Models;
using PuduPrintServer.Services;

namespace PuduPrintServer.Views;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _log = new();
    private readonly Dictionary<string, PrinterCard> _cards = new(); // zona_id → card
    private bool _forceClose; // permite cerrar de verdad (p.ej. al cerrar sesión)

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        LblVersion.Text = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";
        ListLog.ItemsSource = _log;
        UpdateService.Instance.UpdateReady += _ => Dispatcher.Invoke(() =>
            BdrActualizar.Visibility = Visibility.Visible);
        if (UpdateService.Instance.VersionLista != null)
            BdrActualizar.Visibility = Visibility.Visible;
        CargarTarjetas();
        var proc = PrintQueueProcessor.Instance;
        proc.JobCompleted  += OnJobCompleted;
        proc.JobReceiving  += NotifyReceiving;
        proc.OnlineChanged += OnOnlineChanged;
        // "Actividad reciente" se alimenta del mismo LogService que escribe el .txt:
        // UI y archivo muestran exactamente lo mismo.
        LogService.Instance.Escrito += OnLogEscrito;
        Closed += (_, _) =>
        {
            proc.JobCompleted  -= OnJobCompleted;
            proc.JobReceiving  -= NotifyReceiving;
            proc.OnlineChanged -= OnOnlineChanged;
            LogService.Instance.Escrito -= OnLogEscrito;
        };

        ActualizarEstado(proc.Online);
    }

    /// <summary>Header: verde "Activo" con conexión a Supabase; rojo "No hay impresoras en línea"
    /// sin ella. El HTTP local (ticket de caja) sigue funcionando sin red, por eso se muestra igual.</summary>
    private void ActualizarEstado(bool online)
    {
        var http  = ((App)Application.Current).HttpServer;
        var local = http is { IsRunning: true } ? $" · impresión local :{http.Port}" : "";
        LblStatus.Text = online
            ? $"● Activo{local}"
            : $"● No hay impresoras en línea{local}";
        LblStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            online ? System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99)   // verde
                   : System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)); // rojo
    }

    private void OnOnlineChanged(bool online) => Dispatcher.Invoke(() => ActualizarEstado(online));

    private void OnLogEscrito(string linea) => Dispatcher.BeginInvoke(() =>
    {
        _log.Insert(0, linea);
        if (_log.Count > 20) _log.RemoveAt(20);
    });

    private void CargarTarjetas()
    {
        PanelImpresoras.Children.Clear();
        _cards.Clear();
        var imps = DatabaseService.Instance.GetImpresoras().Where(i => i.Activa);
        foreach (var imp in imps)
        {
            var card = new PrinterCard();
            card.SetImpresora(imp);
            _cards[imp.ZonaId] = card;
            PanelImpresoras.Children.Add(card);
        }
    }

    private void OnJobCompleted(PrintJobResult result)
    {
        // La línea de log ya la escribió ImprimirComandaAsync vía LogService → acá solo la tarjeta.
        Dispatcher.Invoke(() =>
        {
            if (_cards.TryGetValue(result.Job.ZonaId, out var card))
            {
                card.SetState(PrinterState.Printing);
                card.JobDone(result.Ok);
            }
        });
    }

    // Notificar a las tarjetas cuando llega un job (llamado desde ProcessJobAsync)
    public void NotifyReceiving(string zonaId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_cards.TryGetValue(zonaId, out var card))
                card.SetState(PrinterState.Receiving);
        });
    }

    public void RefreshCards() => Dispatcher.Invoke(CargarTarjetas);

    private void BdrConfig_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Dialogs.ImpresoraDialog();
        dlg.ShowDialog();
        CargarTarjetas();
    }

    private void BdrLogs_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LogService.Instance.Carpeta}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { LogService.Instance.Error("app", "No se pudo abrir la carpeta de logs", ex); }
    }

    private void BdrActualizar_Click(object sender, MouseButtonEventArgs e)
    {
        var r = MessageBox.Show("¿Reiniciar para aplicar la actualización?",
            "Actualizar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) UpdateService.Instance.ApplyAndRestart();
    }

    private void BdrLogout_Click(object sender, MouseButtonEventArgs e)
    {
        var r = MessageBox.Show("¿Cerrar sesión? Deberás iniciar sesión de nuevo.",
            "Cerrar sesión", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        // Diferir para que el handler termine antes de teardown de ventanas
        Dispatcher.BeginInvoke(new Action(() =>
            ((App)Application.Current).Logout()));
    }

    /// <summary>Cierra la ventana de verdad (omite el minimizar-a-bandeja).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return; // cierre real solicitado
        e.Cancel = true;
        Hide(); // Minimizar a bandeja en lugar de cerrar
    }
}