using System.Windows;
using System.Windows.Controls;
using PuduPrintServer.Services;

namespace PuduPrintServer.Dialogs;

/// <summary>
/// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): asistente "Agregar impresora". Busca térmicas
/// que Windows no tiene (red 9100, USB sin cola, Bluetooth/serie COM), imprime una prueba sin instalar nada y la
/// instala en Windows con "Generic / Text Only" (ver <see cref="InstaladorImpresoras"/>).
/// </summary>
public partial class AgregarImpresoraDialog : Window
{
    private record Opcion(InstaladorImpresoras.Tipo Tipo, string Destino, string Mac, string Texto, string NombreSugerido)
    {
        public override string ToString() => Texto;
    }

    private InstaladorImpresoras.Tipo? _tipo;
    private CancellationTokenSource? _cts;

    public AgregarImpresoraDialog()
    {
        InitializeComponent();
    }

    private async void BtnTipo_Click(object sender, RoutedEventArgs e)
    {
        _tipo = Enum.Parse<InstaladorImpresoras.Tipo>((string)((Button)sender).Tag);
        foreach (var b in new[] { BtnRed, BtnUsb, BtnCom })
            b.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(b == sender ? "#4C1D95" : "#2A2A2A"));
        LblIpFija.Visibility = _tipo == InstaladorImpresoras.Tipo.Red ? Visibility.Visible : Visibility.Collapsed;
        await BuscarAsync();
    }

    private async void BtnBuscar_Click(object sender, RoutedEventArgs e) => await BuscarAsync();

    private async Task BuscarAsync()
    {
        if (_tipo is null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ListaEncontradas.Items.Clear();
        BtnBuscar.IsEnabled = false;
        ActualizarBotones();
        LblEstado.Text = _tipo switch
        {
            InstaladorImpresoras.Tipo.Red => $"Buscando impresoras en la red ({string.Join(", ", DescubrirImpresoras.Subredes().Select(s => s + "x"))})…",
            InstaladorImpresoras.Tipo.Usb => "Buscando impresoras USB conectadas sin instalar…",
            _ => "Buscando puertos Bluetooth / serie…",
        };

        try
        {
            var opciones = new List<Opcion>();
            switch (_tipo)
            {
                case InstaladorImpresoras.Tipo.Red:
                    foreach (var r in await DescubrirImpresoras.BuscarEnRedAsync(ct))
                    {
                        var instalada = ImpresorasAgente.NombresRed()
                            .FirstOrDefault(n => ImpresorasAgente.Red(n) is { } x && (x.Ip == r.Ip || (x.Mac != "" && x.Mac == r.Mac)));
                        var texto = $"{r.Ip}   ·   MAC {(r.Mac == "" ? "?" : r.Mac)}{(instalada is null ? "" : $"   (ya instalada: {instalada})")}";
                        opciones.Add(new Opcion(InstaladorImpresoras.Tipo.Red, r.Ip, r.Mac, texto, $"Térmica red {r.Ip}"));
                    }
                    break;
                case InstaladorImpresoras.Tipo.Usb:
                    foreach (var p in await DescubrirImpresoras.PuertosUsbLibresAsync())
                        opciones.Add(new Opcion(InstaladorImpresoras.Tipo.Usb, p.Puerto, "", $"{p.Puerto}   ·   {p.Descripcion}", $"Térmica USB {p.Puerto}"));
                    break;
                default:
                    foreach (var p in await DescubrirImpresoras.PuertosComAsync())
                        opciones.Add(new Opcion(InstaladorImpresoras.Tipo.Com, p.Puerto, "", p.Descripcion, $"Térmica {p.Puerto}"));
                    break;
            }
            if (ct.IsCancellationRequested) return;

            foreach (var o in opciones) ListaEncontradas.Items.Add(o);
            LblEstado.Text = opciones.Count switch
            {
                0 => _tipo switch
                {
                    InstaladorImpresoras.Tipo.Red => "No se encontró ninguna. Revisa que esté encendida y conectada al mismo router que este PC.",
                    InstaladorImpresoras.Tipo.Usb => "No hay impresoras USB sin instalar. Enchúfala, espera unos segundos y busca de nuevo.",
                    _ => "No hay puertos Bluetooth. Primero emparéjala en Configuración de Windows → Bluetooth y dispositivos.",
                },
                1 => "Se encontró 1. Selecciónala, imprime una prueba e instálala.",
                _ => $"Se encontraron {opciones.Count}. Si no sabes cuál es, imprime una prueba en cada una.",
            };
            if (opciones.Count == 1) ListaEncontradas.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LblEstado.Text = $"Error al buscar: {ex.Message}";
            LogService.Instance.Error("asistente", "Error al buscar impresoras", ex);
        }
        finally
        {
            BtnBuscar.IsEnabled = true;
        }
    }

    private void ListaEncontradas_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListaEncontradas.SelectedItem is Opcion o) TxtNombre.Text = o.NombreSugerido;
        ActualizarBotones();
    }

    private void ActualizarBotones()
    {
        bool hay = ListaEncontradas.SelectedItem is Opcion;
        BtnPrueba.IsEnabled = hay;
        BtnInstalar.IsEnabled = hay;
    }

    private int AnchoPuntos => int.TryParse((CmbAncho.SelectedItem as ComboBoxItem)?.Tag as string, out var a) ? a : 576;

    /// <summary>Prueba directa al dispositivo, antes de instalar nada en Windows.</summary>
    private async void BtnPrueba_Click(object sender, RoutedEventArgs e)
    {
        if (ListaEncontradas.SelectedItem is not Opcion o) return;
        BtnPrueba.IsEnabled = false;
        try
        {
            var bytes = InstaladorImpresoras.TicketPrueba(TxtNombre.Text, AnchoPuntos);
            var printer = new PrinterService();
            switch (o.Tipo)
            {
                case InstaladorImpresoras.Tipo.Red:
                    await printer.PrintIpAsync(bytes, o.Destino);
                    break;
                case InstaladorImpresoras.Tipo.Com:
                    await printer.PrintComAsync(bytes, o.Destino);
                    break;
                default:
                    LblEstado.Text = "Una impresora USB sin instalar no recibe trabajos: instálala y la prueba sale sola.";
                    return;
            }
            LblEstado.Text = "Prueba enviada. Debe salir un ticket con barras negras y cortar el papel.";
        }
        catch (Exception ex)
        {
            LblEstado.Text = $"No se pudo imprimir la prueba: {ex.Message}";
        }
        finally { ActualizarBotones(); }
    }

    private async void BtnInstalar_Click(object sender, RoutedEventArgs e)
    {
        if (ListaEncontradas.SelectedItem is not Opcion o) return;
        BtnInstalar.IsEnabled = BtnPrueba.IsEnabled = false;
        LblEstado.Text = "Instalando en Windows… (puede pedir permiso de administrador)";
        try
        {
            var nombre = TxtNombre.Text.Trim();
            var (ok, msg) = await InstaladorImpresoras.InstalarAsync(
                new InstaladorImpresoras.Pedido(o.Tipo, nombre, o.Destino, o.Mac, 9100, AnchoPuntos));
            if (!ok) { LblEstado.Text = $"No se pudo instalar: {msg}"; return; }

            // Prueba por el camino real del agente (cola de Windows o TCP directo con reconexión).
            try { await ImpresorasAgente.EnviarAsync(nombre, InstaladorImpresoras.TicketPrueba(nombre, AnchoPuntos)); }
            catch (Exception ex) { LogService.Instance.Warn("asistente", $"Instalada, pero la prueba falló: {ex.Message}"); }

            MessageBox.Show(this,
                $"{msg}\n\nEn POS Matic: Configuración → Impresión por Agente local → elige \"{nombre}\".",
                "Impresora instalada", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LblEstado.Text = $"No se pudo instalar: {ex.Message}";
        }
        finally { ActualizarBotones(); }
    }

    private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
