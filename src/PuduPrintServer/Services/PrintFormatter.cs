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
