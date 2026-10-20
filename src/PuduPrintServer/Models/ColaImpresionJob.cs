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
    // Comensales y nota del pedido: van en TODAS las comandas de la mesa (cocina, barra...)
    [JsonPropertyName("comensales")]      public int?    Comensales      { get; set; }
    [JsonPropertyName("notas")]           public string? Notas           { get; set; }
    // IP y puerto YA vienen copiados desde zonas_produccion por POS Matic al encolar
    [JsonPropertyName("impresora_ip")]    public string? ImpresoraIp     { get; set; }
    [JsonPropertyName("impresora_puerto")]public int?    ImpresoraPuerto  { get; set; }
    // Nombre de zona (join opcional, solo para display en log)
    [JsonPropertyName("zona_nombre")]     public string? ZonaNombre      { get; set; }
    // 1.0.6 — rótulo de la comanda v2 (columnas ya existentes en cola_impresion, migración
    // 20261017191459 de pos-matic_no): comanda | adicional | reimpresion | anulacion | marcado.
    // Null = POS viejo → PrintFormatter intenta deducirlo del sufijo de zona_nombre.
    [JsonPropertyName("tipo_comanda")]    public string? TipoComanda     { get; set; }
    // 1.0.6 — número del envío POR ZONA (ADICIONAL #n, solo si tipo_comanda = adicional). Null = POS viejo.
    [JsonPropertyName("secuencia")]       public int?    Secuencia       { get; set; }
    // 1.0.6 — nombre limpio de la zona ("Cocina") que manda el POS por /comanda (`zonaBase`), porque
    // zonaNombre sigue trayendo el rótulo viejo pegado para el agente 1.0.5. En la cola no existe:
    // ahí zona_nombre sale del join con zonas_produccion y ya viene limpio.
    [JsonIgnore]                          public string? ZonaBase        { get; set; }

    // Helper — items como string JSON para el formatter
    public string ItemsJson => Items?.ToString() ?? "[]";
}