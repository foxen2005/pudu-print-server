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
        PrintQueueProcessor.Instance.JobCompleted += OnJobCompleted;
        PrintQueueProcessor.Instance.JobReceiving += NotifyReceiving;
        Closed += (_, _) => PrintQueueProcessor.Instance.JobCompleted -= OnJobCompleted;
    }

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
        Dispatcher.Invoke(() =>
        {
            var zona  = result.Job.ZonaNombre ?? result.Job.ZonaId[..Math.Min(8, result.Job.ZonaId.Length)];
            var mesa  = result.Job.MesaNumero ?? "-";
            var icon  = result.Ok ? "✓" : "✗";
            var hora  = DateTime.Now.ToString("HH:mm");
            _log.Insert(0, $"{hora}  {zona,-12}  Mesa {mesa,-6}  {icon}  {result.Error ?? "impreso"}");
            if (_log.Count > 20) _log.RemoveAt(20);
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

    private void BdrActualizar_Click(object sender, MouseButtonEventArgs e)
    {
        var r = MessageBox.Show("¿Reiniciar para aplicar la actualización?",
            "Actualizar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) UpdateService.Instance.ApplyAndRestart();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide(); // Minimizar a bandeja en lugar de cerrar
    }
}