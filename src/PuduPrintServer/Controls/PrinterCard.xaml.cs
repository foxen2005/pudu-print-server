using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PuduPrintServer.Models;

namespace PuduPrintServer.Controls;

public enum PrinterState { Idle, Receiving, Printing, Error }

public partial class PrinterCard : System.Windows.Controls.UserControl
{
    private int _jobCount;

    public PrinterCard() => InitializeComponent();

    public void SetImpresora(ImpresoraLocal imp)
    {
        LblNombre.Text = imp.ZonaNombre;
        LblTipo.Text   = imp.TipoConexion;
        SetState(PrinterState.Idle);
    }

    // Para impresoras IP (sin entrada local)
    public void SetZona(ZonaProduccion zona)
    {
        LblNombre.Text = zona.Nombre;
        LblTipo.Text   = "IP";
        SetState(PrinterState.Idle);
    }

    public void SetState(PrinterState state)
    {
        StopAllAnimations();
        switch (state)
        {
            case PrinterState.Idle:
                Ring.Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                LblEstado.Text = "En espera";
                LblEstado.Foreground = new SolidColorBrush(Colors.Gray);
                LblIcono.Text = "🖨️";
                break;
            case PrinterState.Receiving:
                Ring.Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                LblEstado.Text = "Recibiendo...";
                LblEstado.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                ((Storyboard)Resources["SbPulse"]).Begin();
                break;
            case PrinterState.Printing:
                Ring.Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                LblEstado.Text = "Imprimiendo...";
                LblEstado.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                ((Storyboard)Resources["SbPrinting"]).Begin();
                break;
            case PrinterState.Error:
                Ring.Stroke = new SolidColorBrush(Colors.Red);
                LblEstado.Text = "Error";
                LblEstado.Foreground = new SolidColorBrush(Colors.Red);
                LblIcono.Text = "⚠️";
                break;
        }
    }

    public void JobDone(bool ok)
    {
        if (ok) _jobCount++;
        LblJobs.Text = _jobCount > 0 ? $"{_jobCount} impresos" : "";
        // Volver a idle tras 2 segundos
        Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.Invoke(() => SetState(ok ? PrinterState.Idle : PrinterState.Error)));
    }

    private void StopAllAnimations()
    {
        ((Storyboard)Resources["SbPulse"]).Stop();
        ((Storyboard)Resources["SbPrinting"]).Stop();
        PaperLine1.Opacity = 0;
        PaperLine2.Opacity = 0;
        PaperLine3.Opacity = 0;
        LblIcono.Text = "🖨️";
    }
}