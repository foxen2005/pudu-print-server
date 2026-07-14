using System.Windows;
using Velopack;

namespace PuduPrintServer;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack: maneja instalación/desinstalación antes que cualquier otra cosa.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // TODO: inicialización principal (tray, login, DB, etc.)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}