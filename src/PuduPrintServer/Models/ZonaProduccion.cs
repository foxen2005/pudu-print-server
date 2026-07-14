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