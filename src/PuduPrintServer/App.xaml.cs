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
    private LocalHttpServer? _httpServer;

    /// <summary>Servidor HTTP local para tickets de caja (accesible mientras la app corre).</summary>
    public LocalHttpServer? HttpServer => _httpServer;

    [STAThread]
    public static void Main()
    {
        // Habilitar code pages heredadas (CP850) para ESC/POS en .NET 8
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        VelopackApp.Build().Run();
        // 1.0.7: proceso elevado que solo instala el driver POS-80 (lo lanza el asistente) y sale.
        if (Environment.GetCommandLineArgs().Contains(DriverPos80.ArgInstalar))
        {
            Environment.ExitCode = DriverPos80.InstalarAqui();
            return;
        }
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

        // Red de seguridad: un error inesperado se loguea y NO cierra el agente.
        var log = LogService.Instance;
        DispatcherUnhandledException += (_, ex) =>
        {
            log.Error("app", "Excepción no controlada (UI)", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            log.Error("app", "Excepción no controlada (AppDomain)", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            log.Error("app", "Excepción no observada (Task)", ex.Exception);
            ex.SetObserved();
        };

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        log.Info("app", $"Pudu Print Server v{v?.Major}.{v?.Minor}.{v?.Build} iniciando — logs en {log.Carpeta}");

        // Servidor HTTP local para el ticket de caja. No depende del login de Supabase
        // (el PDF viaja en el request), por eso arranca antes del gate de sesión.
        try
        {
            _httpServer = new LocalHttpServer();
            _httpServer.Start();
            // 1.0.7: térmicas de red del asistente → reconexión por MAC si cambia la IP.
            RedReconexion.Iniciar();
        }
        catch (Exception ex)
        {
            // No bloquear el arranque del agente si el puerto está ocupado, etc.
            log.Error("local", "No se pudo iniciar el servidor HTTP local", ex);
        }

        // Intentar restaurar sesión. TryRestoreSessionAsync ya no lanza sin red (usa el token
        // guardado); el try/catch es por si falla SQLite u otra cosa inesperada.
        bool ok = false;
        try { ok = await new AuthService().TryRestoreSessionAsync(); }
        catch (Exception ex) { log.Error("auth", "Error restaurando sesión", ex); }
        if (!ok)
        {
            var login = new LoginWindow();
            if (login.ShowDialog() != true) { log.Info("app", "Login cancelado, saliendo"); Shutdown(); return; }
        }

        // Arrancar procesador
        PrintQueueProcessor.Instance.Start();
        _ = UpdateService.Instance.CheckAndDownloadAsync();

        // 1.0.7: arranca con Windows (HKCU Run) y en ese arranque queda solo en la bandeja.
        AutoArranque.Asegurar();
        // 1.0.11: tras una actualización automática con el agente en la bandeja, vuelve a la bandeja.
        bool reinicioOculto = DatabaseService.Instance.GetConfig("reinicio_oculto") == "1";
        if (reinicioOculto) DatabaseService.Instance.DeleteConfig("reinicio_oculto");
        if (e.Args.Contains(AutoArranque.ArgOculto) || reinicioOculto)
        {
            log.Info("app", "Inicio con Windows: el agente queda en la bandeja (doble clic en el ícono para abrirlo)");
            return;
        }

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
        LogService.Instance.Info("auth", "Cierre de sesión");
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
        LogService.Instance.Info("app", "Salir desde la bandeja");
        PrintQueueProcessor.Instance.Stop();
        _httpServer?.Stop();
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
