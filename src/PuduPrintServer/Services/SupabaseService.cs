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
        // Timeout corto: sin red, el default (100 s) dejaba el poll colgado y el estado no cambiaba.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        // 1.0.6: + tipo_comanda, secuencia (bug 3 del plan 2026-10-20: el rótulo ADICIONAL /
        // REIMPRESIÓN / ANULAR se perdía cuando la comanda llegaba por la cola). El ✓ por ítem
        // (ya_enviado) viaja dentro de `items`, no necesita columna.
        var url = $"{BaseUrl}/rest/v1/cola_impresion" +
                  $"?select=id,zona_id,estado,items,mesa_numero,garzon_nombre,pedido_id,comensales,notas" +
                  $",tipo_comanda,secuencia" +
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
