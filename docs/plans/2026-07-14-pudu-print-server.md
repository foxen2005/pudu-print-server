# Pudu Print Server — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** App WPF .NET 8 que corre en la bandeja del sistema, poll-ea `cola_impresion` en Supabase cada 1.5 s y envía jobs ESC/POS a impresoras térmicas USB, Bluetooth o IP según `zona_id`.

**Architecture:** Login Supabase Auth igual que SOS pos. Poll REST sobre `cola_impresion`. Match impresora por job: si `cola_impresion.impresora_ip` tiene valor (ya copiado por POS Matic desde la zona al encolar) usa TcpClient; si no, busca en SQLite local por `zona_id` → SerialPort (COM). PrintFormatter parsea CartItem[] (campos reales: `nombre`, `quantity`, `precio_venta`). UI: bandeja sistema + ventana con tarjetas animadas via WPF Storyboard.

**Tech Stack:** C# WPF .NET 8 · Microsoft.Data.Sqlite · Hardcodet.NotifyIcon.Wpf · Velopack 1.2.0 · HttpClient (Supabase REST directo, sin SDK) · SerialPort · TcpClient

**Supabase:** proyecto `miuirpcrkfnfngvplhqp` · URL `https://miuirpcrkfnfngvplhqp.supabase.co`
**Anon key:** `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im1pdWlycGNya2ZuZm5ndnBsaHFwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Mjk5NTczNDUsImV4cCI6MjA0NTUzMzM0NX0.2hNfyxK_XTP3Fo9-cip0J742bUVSK452j-L78njRE0Q`

---

## Estructura de archivos

```
d:\PUDU\pudu-print-server\
├── docs\specs\   (spec ya existe)
├── docs\plans\   (este archivo)
└── src\PuduPrintServer\
    ├── PuduPrintServer.csproj
    ├── App.xaml
    ├── App.xaml.cs              ← startup, tray, single-instance Mutex
    ├── Assets\
    │   ├── pudu-logo.ico
    │   └── TrayIcon.xaml        ← TaskbarIcon resource
    ├── Models\
    │   ├── ColaImpresionJob.cs
    │   ├── ZonaProduccion.cs
    │   └── ImpresoraLocal.cs
    ├── Services\
    │   ├── UserSession.cs       ← singleton con token y user_id
    │   ├── DatabaseService.cs   ← SQLite config + impresoras
    │   ├── AuthService.cs       ← Supabase Auth login/refresh
    │   ├── SupabaseService.cs   ← poll + patch + GET zonas
    │   ├── EscPos.cs            ← bytes ESC/POS manuales
    │   ├── PrintFormatter.cs    ← items JSON → bytes ESC/POS
    │   ├── PrinterService.cs    ← SerialPort + TcpClient
    │   ├── PrintQueueProcessor.cs ← orquestador con Timer
    │   └── UpdateService.cs     ← Velopack (copia SOS pos)
    ├── Views\
    │   ├── LoginWindow.xaml
    │   ├── LoginWindow.xaml.cs
    │   ├── MainWindow.xaml
    │   └── MainWindow.xaml.cs
    ├── Controls\
    │   ├── PrinterCard.xaml     ← tarjeta con animaciones
    │   └── PrinterCard.xaml.cs
    └── Dialogs\
        ├── ImpresoraDialog.xaml ← agregar/editar impresora
        └── ImpresoraDialog.xaml.cs
```

---

## Task 1: Scaffold del proyecto

**Files:**
- Create: `src\PuduPrintServer\PuduPrintServer.csproj`
- Create: `src\PuduPrintServer\App.xaml`
- Create: `src\PuduPrintServer\App.xaml.cs` (placeholder)

- [ ] **Crear el proyecto WPF**

```powershell
cd "d:\PUDU\pudu-print-server"
dotnet new wpf -n PuduPrintServer -o src\PuduPrintServer --framework net8.0-windows
```

- [ ] **Reemplazar el `.csproj` con la config correcta**

Reemplazar `src\PuduPrintServer\PuduPrintServer.csproj` completo:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyTitle>Pudu Print Server</AssemblyTitle>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <StartupObject>PuduPrintServer.App</StartupObject>
  </PropertyGroup>
  <ItemGroup>
    <ApplicationDefinition Remove="App.xaml" />
    <Page Include="App.xaml" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="2.0.1" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.7" />
    <PackageReference Include="Velopack" Version="1.2.0" />
  </ItemGroup>
</Project>
```

- [ ] **Restaurar paquetes y verificar compilación**

```powershell
cd "d:\PUDU\pudu-print-server\src\PuduPrintServer"
dotnet restore
dotnet build
```

Esperado: `Build succeeded` sin errores.

- [ ] **Crear carpetas de estructura**

```powershell
mkdir Models, Services, Views, Controls, Dialogs, Assets
```

- [ ] **Crear `App.xaml` con namespace correcto** (WPF template lo crea, solo verificar que tenga `x:Class="PuduPrintServer.App"`)

- [ ] **Commit**

```powershell
cd "d:\PUDU\pudu-print-server"
git init
git add .
$msg = "chore: scaffold PuduPrintServer WPF .NET 8`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git commit -m $msg
```

---

## Task 2: Modelos

**Files:**
- Create: `src\PuduPrintServer\Models\ColaImpresionJob.cs`
- Create: `src\PuduPrintServer\Models\ZonaProduccion.cs`
- Create: `src\PuduPrintServer\Models\ImpresoraLocal.cs`

- [ ] **Crear `Models\ColaImpresionJob.cs`**

```csharp
using System.Text.Json.Serialization;

namespace PuduPrintServer.Models;

public class ColaImpresionJob
{
    [JsonPropertyName("id")]              public string  Id              { get; set; } = "";
    [JsonPropertyName("zona_id")]         public string  ZonaId          { get; set; } = "";
    [JsonPropertyName("user_id")]         public string  UserId          { get; set; } = "";
    [JsonPropertyName("estado")]          public string  Estado          { get; set; } = "";
    [JsonPropertyName("items")]           public object? Items           { get; set; }
    [JsonPropertyName("mesa_numero")]     public string? MesaNumero      { get; set; }
    [JsonPropertyName("garzon_nombre")]   public string? GarzonNombre    { get; set; }
    [JsonPropertyName("pedido_id")]       public string? PedidoId        { get; set; }
    // IP y puerto YA vienen copiados desde zonas_produccion por POS Matic al encolar
    [JsonPropertyName("impresora_ip")]    public string? ImpresoraIp     { get; set; }
    [JsonPropertyName("impresora_puerto")]public int?    ImpresoraPuerto  { get; set; }
    // Nombre de zona (join opcional, solo para display en log)
    [JsonPropertyName("zona_nombre")]     public string? ZonaNombre      { get; set; }

    // Helper — items como string JSON para el formatter
    public string ItemsJson => Items?.ToString() ?? "[]";
}
```

- [ ] **Crear `Models\ZonaProduccion.cs`**

```csharp
using System.Text.Json.Serialization;

namespace PuduPrintServer.Models;

public class ZonaProduccion
{
    [JsonPropertyName("id")]               public string Id             { get; set; } = "";
    [JsonPropertyName("nombre")]           public string Nombre         { get; set; } = "";
    [JsonPropertyName("impresora_ip")]     public string? ImpresoraIp   { get; set; }
    [JsonPropertyName("impresora_puerto")] public int?    ImpresoraPuerto { get; set; }
    [JsonPropertyName("activo")]           public bool    Activo         { get; set; }
}
```

- [ ] **Crear `Models\ImpresoraLocal.cs`**

```csharp
namespace PuduPrintServer.Models;

public class ImpresoraLocal
{
    public int    Id            { get; set; }
    public string ZonaId        { get; set; } = "";
    public string ZonaNombre    { get; set; } = "";
    public string TipoConexion  { get; set; } = "USB"; // USB | BT
    public string PuertoCom     { get; set; } = "";    // COM3, COM7...
    public bool   Activa        { get; set; } = true;
}
```

- [ ] **Commit**

```powershell
$msg = "feat: modelos ColaImpresionJob, ZonaProduccion, ImpresoraLocal`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 3: UserSession + DatabaseService

**Files:**
- Create: `src\PuduPrintServer\Services\UserSession.cs`
- Create: `src\PuduPrintServer\Services\DatabaseService.cs`

- [ ] **Crear `Services\UserSession.cs`**

```csharp
namespace PuduPrintServer.Services;

public class UserSession
{
    private static UserSession? _i;
    public static UserSession Instance => _i ??= new UserSession();

    public string? UserId        { get; set; }
    public string? AccessToken   { get; set; }
    public string? RefreshToken  { get; set; }
    public string? NombreNegocio { get; set; }

    public void Clear()
    {
        UserId = null; AccessToken = null;
        RefreshToken = null; NombreNegocio = null;
    }
}
```

- [ ] **Crear `Services\DatabaseService.cs`**

```csharp
using Microsoft.Data.Sqlite;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class DatabaseService
{
    private static DatabaseService? _i;
    public static DatabaseService Instance => _i ??= new DatabaseService();

    private readonly string _db = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "pudu-print-server", "print-server.db");

    private DatabaseService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS config (
                clave TEXT PRIMARY KEY,
                valor TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS impresoras (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                zona_id      TEXT NOT NULL,
                zona_nombre  TEXT NOT NULL,
                tipo_conexion TEXT NOT NULL DEFAULT 'USB',
                puerto_com   TEXT NOT NULL,
                activa       INTEGER NOT NULL DEFAULT 1
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_db}");
        con.Open();
        return con;
    }

    public string? GetConfig(string clave)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT valor FROM config WHERE clave=@c";
        cmd.Parameters.AddWithValue("@c", clave);
        return cmd.ExecuteScalar() as string;
    }

    public void SetConfig(string clave, string valor)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO config(clave,valor) VALUES(@c,@v) ON CONFLICT(clave) DO UPDATE SET valor=@v";
        cmd.Parameters.AddWithValue("@c", clave);
        cmd.Parameters.AddWithValue("@v", valor);
        cmd.ExecuteNonQuery();
    }

    public List<ImpresoraLocal> GetImpresoras()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,zona_id,zona_nombre,tipo_conexion,puerto_com,activa FROM impresoras ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<ImpresoraLocal>();
        while (r.Read())
            list.Add(new ImpresoraLocal {
                Id           = r.GetInt32(0),
                ZonaId       = r.GetString(1),
                ZonaNombre   = r.GetString(2),
                TipoConexion = r.GetString(3),
                PuertoCom    = r.GetString(4),
                Activa       = r.GetInt32(5) == 1
            });
        return list;
    }

    public void GuardarImpresora(ImpresoraLocal imp)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        if (imp.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO impresoras(zona_id,zona_nombre,tipo_conexion,puerto_com,activa)
                VALUES(@zi,@zn,@tc,@pc,@ac)
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE impresoras SET zona_id=@zi,zona_nombre=@zn,tipo_conexion=@tc,
                puerto_com=@pc,activa=@ac WHERE id=@id
                """;
            cmd.Parameters.AddWithValue("@id", imp.Id);
        }
        cmd.Parameters.AddWithValue("@zi", imp.ZonaId);
        cmd.Parameters.AddWithValue("@zn", imp.ZonaNombre);
        cmd.Parameters.AddWithValue("@tc", imp.TipoConexion);
        cmd.Parameters.AddWithValue("@pc", imp.PuertoCom);
        cmd.Parameters.AddWithValue("@ac", imp.Activa ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void EliminarImpresora(int id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM impresoras WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Verificar compilación**

```powershell
dotnet build
```

Esperado: `Build succeeded`.

- [ ] **Commit**

```powershell
$msg = "feat: UserSession y DatabaseService con SQLite`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 4: AuthService + LoginWindow

**Files:**
- Create: `src\PuduPrintServer\Services\AuthService.cs`
- Create: `src\PuduPrintServer\Views\LoginWindow.xaml`
- Create: `src\PuduPrintServer\Views\LoginWindow.xaml.cs`

- [ ] **Crear `Services\AuthService.cs`**

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PuduPrintServer.Services;

public class AuthService
{
    private const string Url     = "https://miuirpcrkfnfngvplhqp.supabase.co";
    private const string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im1pdWlycGNya2ZuZm5ndnBsaHFwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Mjk5NTczNDUsImV4cCI6MjA0NTUzMzM0NX0.2hNfyxK_XTP3Fo9-cip0J742bUVSK452j-L78njRE0Q";

    public async Task<bool> LoginAsync(string email, string password)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("apikey", AnonKey);
        var body = JsonSerializer.Serialize(new { email, password });
        var resp = await http.PostAsync(
            $"{Url}/auth/v1/token?grant_type=password",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var at = root.GetProperty("access_token").GetString()!;
        var rt = root.GetProperty("refresh_token").GetString()!;
        var uid = root.GetProperty("user").GetProperty("id").GetString()!;
        UserSession.Instance.AccessToken  = at;
        UserSession.Instance.RefreshToken = rt;
        UserSession.Instance.UserId       = uid;
        DatabaseService.Instance.SetConfig("access_token",  at);
        DatabaseService.Instance.SetConfig("refresh_token", rt);
        DatabaseService.Instance.SetConfig("user_id",       uid);
        return true;
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        var at  = DatabaseService.Instance.GetConfig("access_token");
        var rt  = DatabaseService.Instance.GetConfig("refresh_token");
        var uid = DatabaseService.Instance.GetConfig("user_id");
        if (at == null || rt == null || uid == null) return false;
        // Refresh token
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("apikey", AnonKey);
        var body = JsonSerializer.Serialize(new { refresh_token = rt });
        var resp = await http.PostAsync(
            $"{Url}/auth/v1/token?grant_type=refresh_token",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
        {
            // Token expirado, usar el viejo igual (puede que aún sirva)
            UserSession.Instance.AccessToken  = at;
            UserSession.Instance.RefreshToken = rt;
            UserSession.Instance.UserId       = uid;
            return true;
        }
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var newAt = root.GetProperty("access_token").GetString()!;
        var newRt = root.GetProperty("refresh_token").GetString()!;
        UserSession.Instance.AccessToken  = newAt;
        UserSession.Instance.RefreshToken = newRt;
        UserSession.Instance.UserId       = uid;
        DatabaseService.Instance.SetConfig("access_token",  newAt);
        DatabaseService.Instance.SetConfig("refresh_token", newRt);
        return true;
    }
}
```

- [ ] **Crear `Views\LoginWindow.xaml`**

```xml
<Window x:Class="PuduPrintServer.Views.LoginWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Pudu Print Server — Login" Width="420" Height="520"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize"
        Background="#0F0F0F">
    <Grid>
        <StackPanel VerticalAlignment="Center" Margin="48,0">
            <!-- Logo + título -->
            <TextBlock Text="🖨" FontSize="48" HorizontalAlignment="Center" Margin="0,0,0,8"/>
            <TextBlock Text="Pudu Print Server" FontSize="22" FontWeight="Bold"
                       Foreground="White" HorizontalAlignment="Center" Margin="0,0,0,4"/>
            <TextBlock Text="Servidor de impresión PUDU" FontSize="12"
                       Foreground="#888" HorizontalAlignment="Center" Margin="0,0,0,40"/>
            <!-- Email -->
            <TextBlock Text="EMAIL" FontSize="11" Foreground="#888" Margin="0,0,0,6"/>
            <TextBox x:Name="TxtEmail" FontSize="14" Padding="12,10"
                     Background="#1A1A1A" Foreground="White"
                     BorderBrush="#333" BorderThickness="1" Margin="0,0,0,16"/>
            <!-- Password -->
            <TextBlock Text="CONTRASEÑA" FontSize="11" Foreground="#888" Margin="0,0,0,6"/>
            <PasswordBox x:Name="TxtPassword" FontSize="14" Padding="12,10"
                         Background="#1A1A1A" Foreground="White"
                         BorderBrush="#333" BorderThickness="1" Margin="0,0,0,8"/>
            <!-- Error -->
            <TextBlock x:Name="LblError" Foreground="#F87171" FontSize="12"
                       Margin="0,0,0,20" Visibility="Collapsed" TextWrapping="Wrap"/>
            <!-- Botón -->
            <Button x:Name="BtnLogin" Content="Ingresar" FontSize="14" FontWeight="Bold"
                    Padding="0,12" Background="#7C3AED" Foreground="White"
                    BorderThickness="0" Cursor="Hand" Click="BtnLogin_Click"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Crear `Views\LoginWindow.xaml.cs`**

```csharp
using System.Windows;
using PuduPrintServer.Services;

namespace PuduPrintServer.Views;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        BtnLogin.IsEnabled = false;
        BtnLogin.Content   = "Ingresando...";
        LblError.Visibility = Visibility.Collapsed;
        try
        {
            var auth = new AuthService();
            bool ok = await auth.LoginAsync(TxtEmail.Text.Trim(), TxtPassword.Password);
            if (ok) { DialogResult = true; Close(); }
            else    { LblError.Text = "Credenciales incorrectas."; LblError.Visibility = Visibility.Visible; }
        }
        catch (Exception ex)
        {
            LblError.Text = $"Error: {ex.Message}";
            LblError.Visibility = Visibility.Visible;
        }
        finally { BtnLogin.IsEnabled = true; BtnLogin.Content = "Ingresar"; }
    }
}
```

- [ ] **Compilar y verificar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: AuthService con login/refresh y LoginWindow`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 5: SupabaseService

**Files:**
- Create: `src\PuduPrintServer\Services\SupabaseService.cs`

- [ ] **Crear `Services\SupabaseService.cs`**

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class SupabaseService
{
    private static SupabaseService? _i;
    public static SupabaseService Instance => _i ??= new SupabaseService();

    private const string BaseUrl = "https://miuirpcrkfnfngvplhqp.supabase.co";
    private const string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im1pdWlycGNya2ZuZm5ndnBsaHFwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Mjk5NTczNDUsImV4cCI6MjA0NTUzMzM0NX0.2hNfyxK_XTP3Fo9-cip0J742bUVSK452j-L78njRE0Q";

    private HttpClient NewClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("apikey", AnonKey);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {UserSession.Instance.AccessToken}");
        return http;
    }

    public async Task<List<ColaImpresionJob>> GetJobsPendientesAsync()
    {
        var uid = UserSession.Instance.UserId;
        // No se hace join con zonas_produccion: impresora_ip/impresora_puerto
        // ya vienen copiados en cola_impresion por POS Matic al encolar.
        // Se agrega zona_nombre opcionalmente via zonas_produccion(nombre) solo para el log.
        var url = $"{BaseUrl}/rest/v1/cola_impresion" +
                  $"?select=id,zona_id,estado,items,mesa_numero,garzon_nombre,pedido_id" +
                  $",impresora_ip,impresora_puerto,zonas_produccion(nombre)" +
                  $"&estado=eq.pendiente&user_id=eq.{uid}&limit=10&order=created_at.asc";
        using var http = NewClient();
        var json = await http.GetStringAsync(url);
        var jobs = JsonSerializer.Deserialize<List<ColaImpresionJob>>(json)
                   ?? new List<ColaImpresionJob>();
        // Aplanar zona_nombre desde el join anidado
        // El JSON trae "zonas_produccion": {"nombre": "cocina"} — extraer manual
        using var doc = JsonDocument.Parse(json);
        for (int i = 0; i < jobs.Count; i++)
        {
            var el = doc.RootElement[i];
            if (el.TryGetProperty("zonas_produccion", out var zp) &&
                zp.ValueKind == JsonValueKind.Object &&
                zp.TryGetProperty("nombre", out var nombre))
                jobs[i].ZonaNombre = nombre.GetString();
        }
        return jobs;
    }

    public async Task PatchEstadoAsync(string jobId, string estado, string? errorMsg = null)
    {
        var url = $"{BaseUrl}/rest/v1/cola_impresion?id=eq.{jobId}";
        var body = errorMsg != null
            ? JsonSerializer.Serialize(new { estado, error_mensaje = errorMsg })
            : JsonSerializer.Serialize(new { estado });
        using var http = NewClient();
        http.DefaultRequestHeaders.Add("Prefer", "return=minimal");
        await http.PatchAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    public async Task<List<ZonaProduccion>> GetZonasAsync()
    {
        var uid = UserSession.Instance.UserId;
        var url = $"{BaseUrl}/rest/v1/zonas_produccion" +
                  $"?user_id=eq.{uid}&activo=eq.true" +
                  $"&select=id,nombre,impresora_ip,impresora_puerto&order=nombre.asc";
        using var http = NewClient();
        var json = await http.GetStringAsync(url);
        return JsonSerializer.Deserialize<List<ZonaProduccion>>(json)
               ?? new List<ZonaProduccion>();
    }
}
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: SupabaseService — poll cola_impresion, patch estado, GET zonas`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 6: EscPos + PrintFormatter

**Files:**
- Create: `src\PuduPrintServer\Services\EscPos.cs`
- Create: `src\PuduPrintServer\Services\PrintFormatter.cs`

- [ ] **Crear `Services\EscPos.cs`**

```csharp
using System.Text;

namespace PuduPrintServer.Services;

public static class EscPos
{
    private static readonly Encoding Cp850 = Encoding.GetEncoding(850);

    public static byte[] Initialize() => [0x1B, 0x40];
    public static byte[] BoldOn()     => [0x1B, 0x45, 0x01];
    public static byte[] BoldOff()    => [0x1B, 0x45, 0x00];
    public static byte[] Center()     => [0x1B, 0x61, 0x01];
    public static byte[] Left()       => [0x1B, 0x61, 0x00];
    public static byte[] BigOn()      => [0x1B, 0x21, 0x30];
    public static byte[] BigOff()     => [0x1B, 0x21, 0x00];
    public static byte[] Cut()        => [0x1D, 0x56, 0x41, 0x03];
    public static byte[] Feed(int n = 1) => Enumerable.Repeat((byte)0x0A, n).ToArray();
    public static byte[] Text(string s)  => Cp850.GetBytes(s);
    public static byte[] Line(string s = "") => Cp850.GetBytes(s + "\n");

    public static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();
}
```

- [ ] **Crear `Services\PrintFormatter.cs`**

Items JSON enviados por POS Matic: array de CartItem[] con campos reales:
- `nombre`: string — nombre del producto
- `quantity`: int — cantidad (NO `cantidad`)
- `precio_venta`: number — precio unitario (NO `precio_unitario`)
- Ejemplo: `[{"nombre":"Cazuela","quantity":2,"precio_venta":5000,"categoria_id":3,...}]`

```csharp
using System.Text.Json;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class PrintFormatter
{
    private record Item(string Nombre, int Cantidad, decimal PrecioVenta);

    public byte[] FormatComanda(ColaImpresionJob job, string negocioNombre)
    {
        var items = ParseItems(job.ItemsJson);
        var ahora = DateTime.Now;
        var zonaNombre = job.ZonaNombre ?? job.ZonaId[..Math.Min(8, job.ZonaId.Length)];

        var parts = new List<byte[]>
        {
            EscPos.Initialize(),
            EscPos.Center(),
            EscPos.BigOn(), EscPos.BoldOn(),
            EscPos.Line(Truncate(negocioNombre.ToUpper(), 24)),
            EscPos.BigOff(), EscPos.BoldOff(),
            EscPos.Line("================================"),
            EscPos.Left(),
            EscPos.BoldOn(),
            EscPos.Line($"  Zona: {zonaNombre}"),
            EscPos.BoldOff(),
        };

        if (!string.IsNullOrEmpty(job.MesaNumero))
            parts.Add(EscPos.Line($"  Mesa: {job.MesaNumero}"));
        if (!string.IsNullOrEmpty(job.GarzonNombre))
            parts.Add(EscPos.Line($"  Garzon: {job.GarzonNombre}"));

        parts.Add(EscPos.Line($"  {ahora:HH:mm}  {ahora:dd/MM/yyyy}"));
        parts.Add(EscPos.Line("--------------------------------"));
        parts.Add(EscPos.BoldOn());
        parts.Add(EscPos.Line("  Cant  Descripcion"));
        parts.Add(EscPos.BoldOff());
        parts.Add(EscPos.Line("--------------------------------"));

        foreach (var item in items)
            parts.Add(EscPos.Line($"  {item.Cantidad,2}x   {Truncate(item.Nombre, 28)}"));

        parts.Add(EscPos.Line("================================"));
        parts.Add(EscPos.Feed(4));
        parts.Add(EscPos.Cut());

        return EscPos.Concat(parts.ToArray());
    }

    private static List<Item> ParseItems(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<Item>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // Campos reales del CartItem de POS Matic:
                // "quantity" (no "cantidad"), "precio_venta" (no "precio_unitario")
                var nombre   = el.TryGetProperty("nombre",      out var n) ? n.GetString() ?? "" : "";
                var cantidad = el.TryGetProperty("quantity",    out var c) ? c.GetInt32()  : 1;
                var precio   = el.TryGetProperty("precio_venta",out var p) ? p.GetDecimal(): 0;
                list.Add(new Item(nombre, cantidad, precio));
            }
            return list;
        }
        catch { return [new Item("(error leyendo items)", 1, 0)]; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: EscPos helper y PrintFormatter (items JSON → bytes ESC/POS)`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 7: PrinterService

**Files:**
- Create: `src\PuduPrintServer\Services\PrinterService.cs`

- [ ] **Crear `Services\PrinterService.cs`**

```csharp
using System.IO.Ports;
using System.Net.Sockets;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class PrinterService
{
    /// <summary>Envía a impresora USB o Bluetooth via SerialPort (COM).</summary>
    public async Task PrintComAsync(byte[] data, string portName)
    {
        await Task.Run(() =>
        {
            using var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 5000
            };
            port.Open();
            port.BaseStream.Write(data, 0, data.Length);
            port.BaseStream.Flush();
        });
    }

    /// <summary>Envía a impresora IP via TCP (puerto 9100).</summary>
    public async Task PrintIpAsync(byte[] data, string ip, int puerto = 9100)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, puerto).WaitAsync(TimeSpan.FromSeconds(5));
        using var stream = tcp.GetStream();
        await stream.WriteAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await stream.FlushAsync();
    }

    /// <summary>Imprime ticket de prueba a una impresora local (USB/BT).</summary>
    public async Task PrintPruebaComAsync(string portName)
    {
        var bytes = EscPos.Concat(
            EscPos.Initialize(),
            EscPos.Center(), EscPos.BoldOn(),
            EscPos.Line("PUDU PRINT SERVER"),
            EscPos.BoldOff(),
            EscPos.Line("Prueba de conexion OK"),
            EscPos.Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            EscPos.Feed(3), EscPos.Cut()
        );
        await PrintComAsync(bytes, portName);
    }

    /// <summary>Imprime ticket de prueba a IP.</summary>
    public async Task PrintPruebaIpAsync(string ip, int puerto = 9100)
    {
        var bytes = EscPos.Concat(
            EscPos.Initialize(),
            EscPos.Center(), EscPos.BoldOn(),
            EscPos.Line("PUDU PRINT SERVER"),
            EscPos.BoldOff(),
            EscPos.Line("Prueba de conexion IP OK"),
            EscPos.Line($"{ip}:{puerto}"),
            EscPos.Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            EscPos.Feed(3), EscPos.Cut()
        );
        await PrintIpAsync(bytes, ip, puerto);
    }
}
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: PrinterService — SerialPort (USB/BT) y TcpClient (IP)`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 8: PrintQueueProcessor

**Files:**
- Create: `src\PuduPrintServer\Services\PrintQueueProcessor.cs`

- [ ] **Crear `Services\PrintQueueProcessor.cs`**

```csharp
using System.Timers;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public record PrintJobResult(ColaImpresionJob Job, bool Ok, string? Error = null);

public class PrintQueueProcessor
{
    private static PrintQueueProcessor? _i;
    public static PrintQueueProcessor Instance => _i ??= new PrintQueueProcessor();

    private readonly SupabaseService  _supa      = SupabaseService.Instance;
    private readonly PrinterService   _printer   = new();
    private readonly PrintFormatter   _formatter = new();
    private readonly DatabaseService  _db        = DatabaseService.Instance;
    private System.Timers.Timer?      _timer;
    private bool                      _running;

    public event Action<PrintJobResult>? JobCompleted;

    public void Start(int intervalMs = 1500)
    {
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += OnTick;
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private async void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_running) return;
        _running = true;
        try
        {
            var jobs = await _supa.GetJobsPendientesAsync();
            foreach (var job in jobs)
                await ProcessJobAsync(job);
        }
        finally { _running = false; }
    }

    private async Task ProcessJobAsync(ColaImpresionJob job)
    {
        // Marcar procesando (atómico: solo si sigue pendiente)
        await _supa.PatchEstadoAsync(job.Id, "procesando");
        try
        {
            var negocio = _db.GetConfig("negocio_nombre") ?? "PUDU";
            var bytes   = _formatter.FormatComanda(job, negocio);

            // Resolver impresora:
            // 1. impresora_ip ya viene en el job (copiado por POS Matic desde zonas_produccion)
            // 2. Si no hay IP → buscar impresora local USB/BT por zona_id en SQLite
            if (!string.IsNullOrEmpty(job.ImpresoraIp))
            {
                await _printer.PrintIpAsync(bytes, job.ImpresoraIp, job.ImpresoraPuerto ?? 9100);
            }
            else
            {
                var imp = _db.GetImpresoras()
                             .FirstOrDefault(i => i.ZonaId == job.ZonaId && i.Activa);
                if (imp == null)
                    throw new Exception($"Sin impresora configurada para zona {job.ZonaNombre ?? job.ZonaId}");
                await _printer.PrintComAsync(bytes, imp.PuertoCom);
            }

            await _supa.PatchEstadoAsync(job.Id, "impreso");
            JobCompleted?.Invoke(new PrintJobResult(job, true));
        }
        catch (Exception ex)
        {
            await _supa.PatchEstadoAsync(job.Id, "error", ex.Message[..Math.Min(ex.Message.Length, 200)]);
            JobCompleted?.Invoke(new PrintJobResult(job, false, ex.Message));
        }
    }
}
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: PrintQueueProcessor — orquestador con Timer, match zona_id → impresora`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 9: UpdateService

**Files:**
- Create: `src\PuduPrintServer\Services\UpdateService.cs`

- [ ] **Crear `Services\UpdateService.cs`** (misma lógica que SOS pos)

```csharp
using Velopack;
using Velopack.Sources;

namespace PuduPrintServer.Services;

public class UpdateService
{
    private static UpdateService? _i;
    public static UpdateService Instance => _i ??= new UpdateService();

    private const string RepoUrl = "https://github.com/hildenX/pudu-print-server";

    private UpdateManager? _mgr;
    private UpdateInfo?    _updateListo;

    public string? VersionLista => _updateListo?.TargetFullRelease.Version.ToString();
    public event Action<string>? UpdateReady;

    private UpdateService() { }

    public async Task CheckAndDownloadAsync()
    {
        try
        {
            _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!_mgr.IsInstalled) return;
            var nueva = await _mgr.CheckForUpdatesAsync();
            if (nueva == null) return;
            await _mgr.DownloadUpdatesAsync(nueva);
            _updateListo = nueva;
            UpdateReady?.Invoke(nueva.TargetFullRelease.Version.ToString());
        }
        catch { /* sin red o sin release — silencioso */ }
    }

    public void ApplyAndRestart()
    {
        if (_mgr != null && _updateListo != null)
            _mgr.ApplyUpdatesAndRestart(_updateListo);
    }
}
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: UpdateService Velopack (repo pudu-print-server)`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 10: MainWindow UI + PrinterCard

**Files:**
- Create: `src\PuduPrintServer\Views\MainWindow.xaml`
- Create: `src\PuduPrintServer\Views\MainWindow.xaml.cs`
- Create: `src\PuduPrintServer\Controls\PrinterCard.xaml`
- Create: `src\PuduPrintServer\Controls\PrinterCard.xaml.cs`

- [ ] **Crear `Controls\PrinterCard.xaml`**

```xml
<UserControl x:Class="PuduPrintServer.Controls.PrinterCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- Animación: pulso (Idle/Recibiendo) -->
        <Storyboard x:Key="SbPulse" RepeatBehavior="Forever">
            <DoubleAnimation Storyboard.TargetName="Ring"
                             Storyboard.TargetProperty="(UIElement.Opacity)"
                             From="0.2" To="1.0" Duration="0:0:0.7"
                             AutoReverse="True"/>
        </Storyboard>
        <!-- Animación: papel saliendo (Imprimiendo) -->
        <Storyboard x:Key="SbPrinting" RepeatBehavior="Forever">
            <DoubleAnimation Storyboard.TargetName="PaperLine1"
                             Storyboard.TargetProperty="(UIElement.Opacity)"
                             From="0" To="1" Duration="0:0:0.3"/>
            <DoubleAnimation Storyboard.TargetName="PaperLine2"
                             Storyboard.TargetProperty="(UIElement.Opacity)"
                             From="0" To="1" Duration="0:0:0.3" BeginTime="0:0:0.3"/>
            <DoubleAnimation Storyboard.TargetName="PaperLine3"
                             Storyboard.TargetProperty="(UIElement.Opacity)"
                             From="0" To="1" Duration="0:0:0.3" BeginTime="0:0:0.6"/>
        </Storyboard>
    </UserControl.Resources>
    <Border Background="#1A1A1A" CornerRadius="12" Padding="16" Margin="8">
        <Grid Width="150">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="60"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <!-- Badge tipo conexión -->
            <Border Grid.Row="0" Background="#2A2A2A" CornerRadius="4"
                    HorizontalAlignment="Right" Padding="6,2">
                <TextBlock x:Name="LblTipo" Text="USB" FontSize="10" Foreground="#888"/>
            </Border>
            <!-- Ícono impresora + ring animado -->
            <Grid Grid.Row="1" HorizontalAlignment="Center" VerticalAlignment="Center">
                <Ellipse x:Name="Ring" Width="56" Height="56"
                         Stroke="#7C3AED" StrokeThickness="2" Fill="Transparent" Opacity="0.2"/>
                <TextBlock x:Name="LblIcono" Text="🖨️" FontSize="32"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
                <!-- Líneas de papel (Printing) -->
                <StackPanel x:Name="PaperLines" VerticalAlignment="Bottom"
                            HorizontalAlignment="Center" Margin="0,46,0,0">
                    <Rectangle x:Name="PaperLine1" Width="20" Height="2"
                               Fill="White" Margin="0,1" Opacity="0"/>
                    <Rectangle x:Name="PaperLine2" Width="16" Height="2"
                               Fill="White" Margin="0,1" Opacity="0"/>
                    <Rectangle x:Name="PaperLine3" Width="12" Height="2"
                               Fill="White" Margin="0,1" Opacity="0"/>
                </StackPanel>
            </Grid>
            <!-- Nombre zona -->
            <TextBlock x:Name="LblNombre" Grid.Row="2" FontSize="14" FontWeight="SemiBold"
                       Foreground="White" HorizontalAlignment="Center"
                       TextTrimming="CharacterEllipsis" MaxWidth="140" Margin="0,8,0,4"/>
            <!-- Estado -->
            <TextBlock x:Name="LblEstado" Grid.Row="3" FontSize="11"
                       Foreground="#888" HorizontalAlignment="Center" Margin="0,0,0,8"/>
            <!-- Contador jobs -->
            <TextBlock x:Name="LblJobs" Grid.Row="4" FontSize="11"
                       Foreground="#555" HorizontalAlignment="Center"/>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Crear `Controls\PrinterCard.xaml.cs`**

```csharp
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
```

- [ ] **Crear `Views\MainWindow.xaml`**

```xml
<Window x:Class="PuduPrintServer.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:PuduPrintServer.Controls"
        Title="Pudu Print Server" Width="900" Height="620"
        WindowStartupLocation="CenterScreen"
        Background="#0F0F0F"
        Closing="Window_Closing">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="60"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="220"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Border Grid.Row="0">
            <Border.Background>
                <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                    <GradientStop Color="#7C3AED" Offset="0"/>
                    <GradientStop Color="#4C1D95" Offset="1"/>
                </LinearGradientBrush>
            </Border.Background>
            <Grid Margin="20,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="🖨️" FontSize="24" VerticalAlignment="Center" Margin="0,0,12,0"/>
                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="Pudu Print Server" FontSize="16" FontWeight="Bold" Foreground="White"/>
                        <TextBlock x:Name="LblVersion" FontSize="10" Foreground="#70FFFFFF"
                                   VerticalAlignment="Bottom" Margin="6,0,0,2"/>
                    </StackPanel>
                    <TextBlock x:Name="LblStatus" Text="● Activo" FontSize="11"
                               Foreground="#34D399"/>
                </StackPanel>
                <!-- Botón actualizar (oculto por defecto) -->
                <Border Grid.Column="2" x:Name="BdrActualizar" Visibility="Collapsed"
                        Background="#20FFFFFF" BorderBrush="#40FFFFFF" BorderThickness="1"
                        CornerRadius="6" Padding="12,6" Cursor="Hand" Margin="0,0,12,0"
                        MouseLeftButtonUp="BdrActualizar_Click" VerticalAlignment="Center">
                    <TextBlock Text="🔄 Actualizar" Foreground="White" FontSize="12" FontWeight="SemiBold"/>
                </Border>
                <!-- Botón configuración -->
                <Border Grid.Column="3" Background="#20FFFFFF" BorderBrush="#40FFFFFF"
                        BorderThickness="1" CornerRadius="6" Padding="12,6"
                        Cursor="Hand" VerticalAlignment="Center"
                        MouseLeftButtonUp="BdrConfig_Click">
                    <TextBlock Text="⚙ Config" Foreground="White" FontSize="12" FontWeight="SemiBold"/>
                </Border>
            </Grid>
        </Border>

        <!-- Panel impresoras -->
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Margin="8,8,8,0">
            <WrapPanel x:Name="PanelImpresoras" Orientation="Horizontal"/>
        </ScrollViewer>

        <!-- Log de jobs recientes -->
        <Border Grid.Row="2" Background="#111" BorderBrush="#222" BorderThickness="0,1,0,0">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="32"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0" Text="  Actividad reciente"
                           Foreground="#555" FontSize="11" VerticalAlignment="Center"/>
                <ListView x:Name="ListLog" Grid.Row="1" Background="Transparent"
                          BorderThickness="0" Foreground="#888" FontFamily="Consolas" FontSize="11">
                    <ListView.ItemContainerStyle>
                        <Style TargetType="ListViewItem">
                            <Setter Property="Padding" Value="8,2"/>
                            <Setter Property="Background" Value="Transparent"/>
                            <Setter Property="BorderThickness" Value="0"/>
                        </Style>
                    </ListView.ItemContainerStyle>
                </ListView>
            </Grid>
        </Border>
    </Grid>
</Window>
```

- [ ] **Crear `Views\MainWindow.xaml.cs`**

```csharp
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

    public void RefreshCards() => CargarTarjetas();

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
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: MainWindow con tarjetas animadas y log de jobs`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 11: ImpresoraDialog (panel de configuración)

**Files:**
- Create: `src\PuduPrintServer\Dialogs\ImpresoraDialog.xaml`
- Create: `src\PuduPrintServer\Dialogs\ImpresoraDialog.xaml.cs`

- [ ] **Crear `Dialogs\ImpresoraDialog.xaml`**

```xml
<Window x:Class="PuduPrintServer.Dialogs.ImpresoraDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Configuración de impresoras" Width="560" Height="600"
        WindowStartupLocation="CenterOwner" Background="#111"
        ResizeMode="NoResize">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Lista de impresoras configuradas -->
        <StackPanel Grid.Row="0">
            <TextBlock Text="Impresoras configuradas" FontSize="16" FontWeight="Bold"
                       Foreground="White" Margin="0,0,0,12"/>
            <ListView x:Name="ListImpresoras" Background="#1A1A1A" BorderBrush="#333"
                      Foreground="White" Height="180" SelectionChanged="ListImpresoras_SelectionChanged">
                <ListView.View>
                    <GridView>
                        <GridViewColumn Header="Zona" Width="120" DisplayMemberBinding="{Binding ZonaNombre}"/>
                        <GridViewColumn Header="Tipo" Width="60"  DisplayMemberBinding="{Binding TipoConexion}"/>
                        <GridViewColumn Header="Puerto COM" Width="100" DisplayMemberBinding="{Binding PuertoCom}"/>
                    </GridView>
                </ListView.View>
            </ListView>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0" HorizontalAlignment="Right">
                <Button Content="Eliminar" x:Name="BtnEliminar" Click="BtnEliminar_Click"
                        Background="#7F1D1D" Foreground="White" BorderThickness="0"
                        Padding="12,6" Margin="0,0,8,0" IsEnabled="False"/>
                <Button Content="Probar conexión" x:Name="BtnProbar" Click="BtnProbar_Click"
                        Background="#1E3A5F" Foreground="White" BorderThickness="0"
                        Padding="12,6" IsEnabled="False"/>
            </StackPanel>
        </StackPanel>

        <!-- Formulario agregar/editar -->
        <StackPanel Grid.Row="1" Margin="0,20,0,0">
            <TextBlock Text="Agregar impresora USB / Bluetooth"
                       FontSize="14" FontWeight="SemiBold" Foreground="White" Margin="0,0,0,12"/>
            <!-- Zona -->
            <TextBlock Text="ZONA DE PRODUCCIÓN" FontSize="11" Foreground="#888" Margin="0,0,0,6"/>
            <ComboBox x:Name="CmbZona" FontSize="13" Padding="10,8" Margin="0,0,0,14"
                      Background="#1A1A1A" Foreground="White" BorderBrush="#333"
                      DisplayMemberPath="Nombre"/>
            <!-- Tipo -->
            <TextBlock Text="TIPO DE CONEXIÓN" FontSize="11" Foreground="#888" Margin="0,0,0,6"/>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,14">
                <RadioButton x:Name="RbUsb" Content="USB" GroupName="Tipo"
                             Foreground="White" IsChecked="True" Margin="0,0,16,0"/>
                <RadioButton x:Name="RbBt"  Content="Bluetooth" GroupName="Tipo"
                             Foreground="White"/>
            </StackPanel>
            <!-- Puerto COM -->
            <TextBlock Text="PUERTO COM (ej: COM3)" FontSize="11" Foreground="#888" Margin="0,0,0,6"/>
            <TextBox x:Name="TxtCom" FontSize="13" Padding="10,8" Margin="0,0,0,16"
                     Background="#1A1A1A" Foreground="White" BorderBrush="#333" BorderThickness="1"/>
            <!-- Estado -->
            <TextBlock x:Name="LblMsg" FontSize="11" Foreground="#888" Margin="0,0,0,8"/>
            <!-- Agregar -->
            <Button Content="Agregar impresora" Click="BtnAgregar_Click"
                    Background="#7C3AED" Foreground="White" BorderThickness="0"
                    Padding="0,10" FontSize="13" FontWeight="Bold"/>
        </StackPanel>

        <!-- Cerrar -->
        <Button Grid.Row="2" Content="Cerrar" Click="BtnCerrar_Click"
                Background="#222" Foreground="White" BorderThickness="0"
                Padding="0,10" FontSize="13" Margin="0,16,0,0"/>
    </Grid>
</Window>
```

- [ ] **Crear `Dialogs\ImpresoraDialog.xaml.cs`**

```csharp
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
```

- [ ] **Compilar**

```powershell
dotnet build
```

- [ ] **Commit**

```powershell
$msg = "feat: ImpresoraDialog — agregar/eliminar/probar impresoras USB y BT`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 12: App.xaml — startup, bandeja del sistema, single-instance

**Files:**
- Modify: `src\PuduPrintServer\App.xaml`
- Modify: `src\PuduPrintServer\App.xaml.cs`
- Create: `src\PuduPrintServer\Assets\TrayIcon.xaml` (resource dict con TaskbarIcon)

- [ ] **Crear `Assets\TrayIcon.xaml`** (ResourceDictionary con el TaskbarIcon de la bandeja)

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:tb="http://www.hardcodet.net/taskbar">
    <tb:TaskbarIcon x:Key="TrayIcon"
                    ToolTipText="Pudu Print Server"
                    IconSource="/Assets/pudu-logo.ico">
        <tb:TaskbarIcon.ContextMenu>
            <ContextMenu>
                <MenuItem Header="Abrir panel"  Click="MenuAbrir_Click"/>
                <Separator/>
                <MenuItem Header="Salir" Click="MenuSalir_Click"/>
            </ContextMenu>
        </tb:TaskbarIcon.ContextMenu>
    </tb:TaskbarIcon>
</ResourceDictionary>
```

**Nota:** los handlers `MenuAbrir_Click` y `MenuSalir_Click` deben estar en `App.xaml.cs`.
Para que funcionen, en WPF los handlers de un ResourceDictionary de App se declaran en `App.xaml.cs` directamente.
Alternativa más simple: usar `tb:TaskbarIcon.TrayLeftMouseDoubleClick` y un ContextMenu inline definido en `App.xaml`.

- [ ] **Reemplazar `App.xaml`**

```xml
<Application x:Class="PuduPrintServer.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:tb="http://www.hardcodet.net/taskbar">
    <Application.Resources>
        <tb:TaskbarIcon x:Key="TrayIcon"
                        ToolTipText="Pudu Print Server"
                        IconSource="/Assets/pudu-logo.ico"
                        TrayLeftMouseDoubleClick="TrayIcon_DoubleClick">
            <tb:TaskbarIcon.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="Abrir panel"  Click="MenuAbrir_Click"/>
                    <Separator/>
                    <MenuItem Header="Salir" Click="MenuSalir_Click"/>
                </ContextMenu>
            </tb:TaskbarIcon.ContextMenu>
        </tb:TaskbarIcon>
    </Application.Resources>
</Application>
```

- [ ] **Reemplazar `App.xaml.cs`**

```csharp
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
```

- [ ] **Agregar ícono placeholder** — copiar `pudu-logo.ico` de SOS pos o crear uno mínimo:

```powershell
# Copiar ícono de SOS pos como placeholder
Copy-Item "d:\PUDU\SOS pos\sos-net\SosPosNet\Assets\sos.ico" `
          "d:\PUDU\pudu-print-server\src\PuduPrintServer\Assets\pudu-logo.ico"
```

Luego en `.csproj` agregar:
```xml
<ItemGroup>
  <Resource Include="Assets\pudu-logo.ico" />
</ItemGroup>
```
Y en el PropertyGroup: `<ApplicationIcon>Assets\pudu-logo.ico</ApplicationIcon>`

- [ ] **Compilar y correr en dev**

```powershell
cd "d:\PUDU\pudu-print-server\src\PuduPrintServer"
dotnet run
```

Verificar: aparece ventana de login → ingresa credenciales → ventana principal con panel vacío → ícono en bandeja del sistema.

- [ ] **Commit**

```powershell
$msg = "feat: App.xaml startup completo — bandeja, single-instance, login, processor`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg
```

---

## Task 13: GitHub repo + primera release v1.0.0

- [ ] **Crear repo en GitHub**

```powershell
cd "d:\PUDU\pudu-print-server"
gh repo create hildenX/pudu-print-server --private --source=. --remote=origin --push
```

- [ ] **Publicar**

```powershell
cd "d:\PUDU\pudu-print-server\src\PuduPrintServer"
dotnet publish -c Release -r win-x64 --self-contained true -o publish /p:PublishSingleFile=false
```

Esperado: `Build succeeded`, carpeta `publish\` con los binarios.

- [ ] **Empaquetar con Velopack**

```powershell
vpk pack -u PuduPrintServer -v 1.0.0 -p publish -o velopack-releases --icon "Assets\pudu-logo.ico"
```

Esperado: `SosPosNet-win-Setup.exe` (en realidad `PuduPrintServer-win-Setup.exe`) generado en `velopack-releases\`.

- [ ] **Subir release a GitHub**

```powershell
$token = gh auth token
vpk upload github --repoUrl https://github.com/hildenX/pudu-print-server --publish -o velopack-releases --token $token
```

Esperado: release `1.0.0` publicada en GitHub.

- [ ] **Verificar instalador local**

```
d:\PUDU\pudu-print-server\src\PuduPrintServer\velopack-releases\PuduPrintServer-win-Setup.exe
```

- [ ] **Commit final**

```powershell
cd "d:\PUDU\pudu-print-server"
$msg = "chore: primera release v1.0.0 — Velopack empaquetado y subido`n`nCo-Authored-By: Felix <noreply@anthropic.com>"
git add .; git commit -m $msg; git push
```

---

## Self-review

**Cobertura del spec:**
- ✅ Login Supabase Auth (Task 4)
- ✅ Poll `cola_impresion` + PATCH estado (Task 5)
- ✅ GET `zonas_produccion` para combobox en ImpresoraDialog (Task 5 + Task 11)
- ✅ ESC/POS bytes manuales (Task 6)
- ✅ PrintFormatter: CartItem[] JSON (campos reales: `nombre`, `quantity`, `precio_venta`) → ticket (Task 6)
- ✅ PrinterService USB/BT SerialPort + IP TcpClient (Task 7)
- ✅ Resolución IP directa desde `job.ImpresoraIp` (copiado por POS Matic) vs local SQLite (Task 8)
- ✅ PrintQueueProcessor con Timer 1.5s (Task 8)
- ✅ UpdateService Velopack (Task 9)
- ✅ Bandeja del sistema single-instance (Task 12)
- ✅ MainWindow con tarjetas animadas (Task 10)
- ✅ PrinterCard con estados Idle/Receiving/Printing/Error + Storyboard (Task 10)
- ✅ ImpresoraDialog agregar/eliminar/probar (Task 11)
- ✅ Primera release v1.0.0 (Task 13)
- ✅ `dotnet run` funciona sin Velopack (guard en UpdateService)

**Tipos consistentes en todo el plan:** `ColaImpresionJob` (sin `ZonaProduccion` anidada, IP viene directa del job), `ZonaProduccion`, `ImpresoraLocal`, `PrintJobResult`, `PrinterState` — todos definidos antes de usarse.

**Nota sobre zonas_categorias:** La separación de items por categoría/zona la hace POS Matic ANTES de encolar. El print-server recibe `items` ya filtrados por zona — no necesita conocer zonas_categorias.