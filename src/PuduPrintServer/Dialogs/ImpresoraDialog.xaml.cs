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
        try
        {
            _zonas = await SupabaseService.Instance.GetZonasAsync();
            CmbZona.ItemsSource = _zonas;
            if (_zonas.Count > 0) CmbZona.SelectedIndex = 0;
        }
        catch { LblMsg.Text = "No se pudieron cargar las zonas de Supabase."; }
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
        if (string.IsNullOrWhiteSpace(TxtCom.Text))
        { LblMsg.Text = "Ingresa el puerto COM."; return; }

        DatabaseService.Instance.GuardarImpresora(new ImpresoraLocal
        {
            ZonaId       = zona.Id,
            ZonaNombre   = zona.Nombre,
            TipoConexion = RbUsb.IsChecked == true ? "USB" : "BT",
            PuertoCom    = TxtCom.Text.Trim().ToUpper(),
            Activa       = true
        });
        LblMsg.Text  = $"Impresora para '{zona.Nombre}' agregada.";
        TxtCom.Text  = "";
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
        LblMsg.Text = $"Probando {_seleccionada.PuertoCom}...";
        try
        {
            await new PrinterService().PrintPruebaComAsync(_seleccionada.PuertoCom);
            LblMsg.Text = "✓ Ticket de prueba enviado correctamente.";
        }
        catch (Exception ex)
        {
            LblMsg.Text = $"✗ Error: {ex.Message}";
        }
    }

    private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();
}
