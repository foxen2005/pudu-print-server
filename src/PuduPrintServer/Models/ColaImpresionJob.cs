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