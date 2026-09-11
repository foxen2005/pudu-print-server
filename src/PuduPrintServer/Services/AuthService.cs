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
        LogService.Instance.Info("auth", $"Login OK ({email})");
        return true;
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        var at  = DatabaseService.Instance.GetConfig("access_token");
        var rt  = DatabaseService.Instance.GetConfig("refresh_token");
        var uid = DatabaseService.Instance.GetConfig("user_id");
        if (at == null || rt == null || uid == null) return false;

        // Sesión guardada en memoria ANTES del refresh: si no hay red, el agente arranca igual
        // con el token viejo (el poll reintenta el refresh solo cuando vuelva la conexión).
        UserSession.Instance.AccessToken  = at;
        UserSession.Instance.RefreshToken = rt;
        UserSession.Instance.UserId       = uid;

        // Refresh token. Timeout corto: el default (100 s) colgaba el arranque sin red.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("apikey", AnonKey);
        var body = JsonSerializer.Serialize(new { refresh_token = rt });
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(
                $"{Url}/auth/v1/token?grant_type=refresh_token",
                new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogService.Instance.Warn("auth", $"Sin red al restaurar sesión, usando token guardado — {ex.Message}");
            return true;
        }
        if (!resp.IsSuccessStatusCode)
        {
            // Token expirado, usar el viejo igual (puede que aún sirva)
            LogService.Instance.Warn("auth", $"Refresh de sesión rechazado ({(int)resp.StatusCode}), usando token guardado");
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
        LogService.Instance.Info("auth", "Sesión restaurada (token renovado)");
        return true;
    }
}
