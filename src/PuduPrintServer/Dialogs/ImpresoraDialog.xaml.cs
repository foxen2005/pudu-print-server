using System.Printing;
using System.Windows;
using System.Windows.Controls;
using PuduPrintServer.Models;
using PuduPrintServer.Services;

namespace PuduPrintServer.Dialogs;

public partial class ImpresoraDialog : Window
{
    private List<ZonaProduccion> _zonas = new();
    private ImpresoraLocal?      _seleccionada;

    public ImpresoraDialog()
    {
        InitializeComponent();
        CargarDatos();
    }

    private async void CargarDatos()
    {
        RefrescarLista();
        CargarImpresorasWindows();
        try
        {
            _zonas = await SupabaseService.Instance.GetZonasAsync();
            CmbZona.ItemsSource = _zonas;
            if (_zonas.Count > 0) CmbZona.SelectedIndex = 0;
        }
        catch { LblMsg.Text = "No se pudieron cargar las zonas de Supabase."; }
    }

    /// <summary>Lista las impresoras instaladas en Windows en el desplegable.</summary>
    private void CargarImpresorasWindows()
    {
        try
        {
            using var server = new LocalPrintServer();
            var nombres = server
                .GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                .Select(q => q.FullName)
                .OrderBy(n => n)
                .ToList();
            CmbImpresoraWin.ItemsSource = nombres;
            if (nombres.Count > 0) CmbImpresoraWin.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            LblMsg.Text = "No se pudieron listar las impresoras de Windows: " + ex.Message;
        }
    }

    private void TipoConexion_Changed(object sender, RoutedEventArgs e)
    {
        // Puede dispararse durante InitializeComponent antes de que existan los paneles
        if (PanelWin == null || PanelCom == null) return;
        bool win = RbWin.IsChecked == true;
        PanelWin.Visibility = win ? Visibility.Visible : Visibility.Collapsed;
        PanelCom.Visibility = win ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefrescarLista()
    {
        ListImpresoras.ItemsSource = null;
        ListImpresoras.ItemsSource = DatabaseService.Instance.GetImpresoras();
    }

    private void ListImpresoras_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _seleccionada = ListImpresoras.SelectedItem as ImpresoraLocal;
        BtnEliminar.IsEnabled = _seleccionada != null;
        BtnProbar.IsEnabled   = _seleccionada != null;
    }

    private void BtnAgregar_Click(object sender, RoutedEventArgs e)
    {
        if (CmbZona.SelectedItem is not ZonaProduccion zona)
        { LblMsg.Text = "Selecciona una zona."; return; }

        string tipo, puerto = "", nombre = "";
        if (RbWin.IsChecked == true)
        {
            if (CmbImpresoraWin.SelectedItem is not string pn || string.IsNullOrWhiteSpace(pn))
            { LblMsg.Text = "Selecciona una impresora de Windows."; return; }
            tipo = "WIN"; nombre = pn;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TxtCom.Text))
            { LblMsg.Text = "Ingresa el puerto COM."; return; }
            tipo = "COM"; puerto = TxtCom.Text.Trim().ToUpper();
        }

        DatabaseService.Instance.GuardarImpresora(new ImpresoraLocal
        {
            ZonaId          = zona.Id,
            ZonaNombre      = zona.Nombre,
            TipoConexion    = tipo,
            PuertoCom       = puerto,
            NombreImpresora = nombre,
            Activa          = true
        });
        LblMsg.Text = $"Impresora para '{zona.Nombre}' agregada.";
        TxtCom.Text = "";
        RefrescarLista();
    }

    private void BtnEliminar_Click(object sender, RoutedEventArgs e)
    {
        if (_seleccionada == null) return;
        var r = MessageBox.Show($"¿Eliminar impresora para '{_seleccionada.ZonaNombre}'?",
            "Confirmar", MessageBoxButton.YesNo);
        if (r == MessageBoxResult.Yes)
        {
            DatabaseService.Instance.EliminarImpresora(_seleccionada.Id);
            RefrescarLista();
        }
    }

    private async void BtnProbar_Click(object sender, RoutedEventArgs e)
    {
        if (_seleccionada == null) return;
        LblMsg.Text = $"Probando {_seleccionada.Destino}...";
        try
        {
            var svc = new PrinterService();
            if (_seleccionada.TipoConexion == "WIN")
                await svc.PrintPruebaSpoolerAsync(_seleccionada.NombreImpresora);
            else
                await svc.PrintPruebaComAsync(_seleccionada.PuertoCom);
            LblMsg.Text = "✓ Ticket de prueba enviado correctamente.";
        }
        catch (Exception ex)
        {
            LblMsg.Text = $"✗ Error: {ex.Message}";
        }
    }

    private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnCerrarSesion_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("¿Cerrar sesión? Deberás iniciar sesión de nuevo.",
            "Cerrar sesión", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        Close(); // cerrar este diálogo modal primero
        // Diferir el logout hasta que el diálogo termine de cerrarse
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            ((App)Application.Current).Logout()));
    }
}
