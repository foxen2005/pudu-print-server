using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (Jean: "los drivers dentro del instalador, así el asistente puede instalarlos sin problemas"): driver
/// "POS-80-Series Driver" (Unidrv + GPD del fabricante, sacado del instalador POS-80-Series.exe de la PRT-8080).
/// Los 5 archivos propios viajan en Drivers\POS80 junto al exe; las DLL base de Unidrv vienen en todo Windows
/// (DriverStore\ntprint.inf_amd64_*\Amd64). Se instala con AddPrinterDriverEx (no hay INF), lo que exige
/// administrador: el agente se relanza elevado con <see cref="ArgInstalar"/> y sale.
/// Con el driver, la cola queda "de verdad" para cualquier programa; el ticket del agente igual sale como imagen
/// ESC/POS (raster), con o sin driver.
/// </summary>
public static class DriverPos80
{
    public const string Nombre = "POS-80-Series Driver";
    public const string ArgInstalar = "--instalar-driver-pos80";
    private const string Entorno = "Windows x64";

    private static readonly string[] ArchivosPropios =
        ["POS-80-Series.gpd", "POS-80-Series.ini", "POS-80-SeriesDrv.dll", "POS-80-SeriesRES.dll", "POS-80-SeriesUI.dll"];

    private static string CarpetaPaquete => Path.Combine(AppContext.BaseDirectory, "Drivers", "POS80");

    public static bool Incluido => ArchivosPropios.All(f => File.Exists(Path.Combine(CarpetaPaquete, f)));

    public static bool DriverExiste()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Control\Print\Environments\{Entorno}\Drivers\Version-3\{Nombre}");
        return k is not null;
    }

    /// <summary>Relanza el agente elevado (UAC) para instalar el driver; true si quedó instalado.</summary>
    public static async Task<bool> InstalarElevadoAsync()
    {
        if (DriverExiste()) return true;
        if (!Incluido) return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, ArgInstalar) { UseShellExecute = true, Verb = "runas" })!;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("driver", $"No se instaló el driver POS-80: {ex.Message}");
            return false;
        }
        var ok = DriverExiste();
        LogService.Instance.Info("driver", ok ? "Driver POS-80 instalado" : "El driver POS-80 no quedó instalado");
        return ok;
    }

    /// <summary>Lo corre el proceso elevado (Main con <see cref="ArgInstalar"/>). Devuelve el código de salida.</summary>
    public static int InstalarAqui()
    {
        try
        {
            var baseUnidrv = CarpetaUnidrv() ?? throw new Exception("No se encontraron los archivos base de Unidrv (ntprint)");
            string Propio(string f) => Path.Combine(CarpetaPaquete, f);
            string Base(string f) => Path.Combine(baseUnidrv, f);

            var dependientes = new[]
            {
                Propio("POS-80-Series.ini"), Propio("POS-80-SeriesDrv.dll"), Propio("POS-80-SeriesRES.dll"),
                Propio("POS-80-SeriesUI.dll"), Base("UNIRES.DLL"), Base("STDNAMES.GPD"),
            };
            var info = new DRIVER_INFO_3
            {
                cVersion = 3,
                pName = Nombre,
                pEnvironment = Entorno,
                pDriverPath = Base("UNIDRV.DLL"),
                pDataFile = Propio("POS-80-Series.gpd"),
                pConfigFile = Base("UNIDRVUI.DLL"),
                pHelpFile = Base("UNIDRV.HLP"),
                pDependentFiles = string.Join("\0", dependientes) + "\0\0",
                pMonitorName = null,
                pDefaultDataType = null,
            };
            if (!AddPrinterDriverEx(null, 3, ref info, APD_COPY_NEW_FILES | APD_COPY_FROM_DIRECTORY_NOT | APD_COPY_ALL_FILES))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return 0;
        }
        catch (Exception ex)
        {
            try { LogService.Instance.Error("driver", "Instalación del driver POS-80 (elevado)", ex); } catch { }
            return 1;
        }
    }

    /// <summary>Carpeta con UNIDRV.DLL: la de drivers del spooler si ya está, si no la del paquete ntprint.</summary>
    private static string? CarpetaUnidrv()
    {
        var spool = Path.Combine(Environment.SystemDirectory, @"spool\drivers\x64\3");
        if (File.Exists(Path.Combine(spool, "UNIDRV.DLL")) && File.Exists(Path.Combine(spool, "UNIRES.DLL"))) return spool;
        var repo = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
        return Directory.Exists(repo)
            ? Directory.GetDirectories(repo, "ntprint.inf_amd64_*")
                .Select(d => Path.Combine(d, "Amd64"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "UNIDRV.DLL")))
            : null;
    }

    // ── winspool ────────────────────────────────────────────────────────────
    private const uint APD_COPY_ALL_FILES = 0x00000004;
    private const uint APD_COPY_NEW_FILES = 0x00000008;
    private const uint APD_COPY_FROM_DIRECTORY_NOT = 0x00000000; // (sin APD_COPY_FROM_DIRECTORY: rutas completas)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DRIVER_INFO_3
    {
        public uint cVersion;
        public string pName;
        public string pEnvironment;
        public string pDriverPath;
        public string pDataFile;
        public string pConfigFile;
        public string pHelpFile;
        public string pDependentFiles;
        public string? pMonitorName;
        public string? pDefaultDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "AddPrinterDriverExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AddPrinterDriverEx(string? pName, uint level, ref DRIVER_INFO_3 pDriverInfo, uint dwFileCopyFlags);
}
