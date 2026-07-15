using System.Linq;
using System.Threading;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using PuduPrintServer.Services;
using PuduPrintServer.Views;
using Velopack;

namespace PuduPrintServer;

public partial class App : Application
{
    private static Mutex? _mutex;
    private TaskbarIcon?  _tray;

    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        _mutex = new Mutex(true, "PuduPrintServer.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Pudu Print Server ya está en ejecución.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
        _mutex.ReleaseMutex();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _tray = (TaskbarIcon)FindResource("TrayIcon");

        // Intentar restaurar sesión
        var auth = new AuthService();
        bool ok = await auth.TryRestoreSessionAsync();
        if (!ok)
        {
            var login = new LoginWindow();
            if (login.ShowDialog() != true) { Shutdown(); return; }
        }

        // Arrancar procesador
        PrintQueueProcessor.Instance.Start();
        _ = UpdateService.Instance.CheckAndDownloadAsync();

        // Abrir ventana principal
        new MainWindow().Show();
    }

    /// <summary>
    /// Cierra la sesión actual: limpia credenciales, detiene el procesador y
    /// vuelve a mostrar la pantalla de login. Si el usuario cancela el login,
    /// la aplicación se cierra.
    /// </summary>
    public void Logout()
    {
        // 1. Detener procesamiento y limpiar credenciales
        PrintQueueProcessor.Instance.Stop();
        UserSession.Instance.Clear();
        DatabaseService.Instance.DeleteConfig("access_token");
        DatabaseService.Instance.DeleteConfig("refresh_token");
        DatabaseService.Instance.DeleteConfig("user_id");

        // 2. Cerrar todas las ventanas abiertas (principal + diálogos)
        foreach (var w in Current.Windows.OfType<Window>().ToList())
        {
            if (w is MainWindow mw) mw.ForceClose();
            else if (w is not LoginWindow) w.Close();
        }

        // 3. Volver al login
        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            PrintQueueProcessor.Instance.Start();
            new MainWindow().Show();
        }
        else
        {
            PrintQueueProcessor.Instance.Stop();
            _tray?.Dispose();
            Shutdown();
        }
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => AbrirVentana();

    private void MenuAbrir_Click(object sender, RoutedEventArgs e) => AbrirVentana();

    private void MenuSalir_Click(object sender, RoutedEventArgs e)
    {
        PrintQueueProcessor.Instance.Stop();
        _tray?.Dispose();
        Shutdown();
    }

    private static void AbrirVentana()
    {
        var win = Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (win == null) new MainWindow().Show();
        else { win.Show(); win.Activate(); }
    }
}
