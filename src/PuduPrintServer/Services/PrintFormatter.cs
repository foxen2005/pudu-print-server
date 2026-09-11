using System.Text.Json;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class PrintFormatter
{
    private record Item(string Nombre, int Cantidad, decimal PrecioVenta, string? Descripcion);

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
        if (job.Comensales is > 0)
        {
            parts.Add(EscPos.BoldOn());
            parts.Add(EscPos.Line($"  Comensales: {job.Comensales}"));
            parts.Add(EscPos.BoldOff());
        }

        parts.Add(EscPos.Line($"  {ahora:HH:mm}  {ahora:dd/MM/yyyy}"));
        parts.Add(EscPos.Line("--------------------------------"));

        // Nota del pedido: antes de los productos y en negrita, para que cocina la lea primero.
        // Se imprime en cada zona (la misma nota sale en cocina y en barra).
        if (!string.IsNullOrWhiteSpace(job.Notas))
        {
            parts.Add(EscPos.BoldOn());
            parts.Add(EscPos.Line("  * NOTA:"));
            foreach (var linea in Wrap(job.Notas.Trim(), 28))
                parts.Add(EscPos.Line($"    {linea}"));
            parts.Add(EscPos.BoldOff());
            parts.Add(EscPos.Line("--------------------------------"));
        }
        parts.Add(EscPos.BoldOn());
        parts.Add(EscPos.Line("  Cant  Descripcion"));
        parts.Add(EscPos.BoldOff());
        parts.Add(EscPos.Line("--------------------------------"));

        foreach (var item in items)
        {
            parts.Add(EscPos.Line($"  {item.Cantidad,2}x   {Truncate(item.Nombre, 28)}"));
            // Nota por producto ("sin cebolla"): antes solo salía en el PDF, no en la térmica.
            if (!string.IsNullOrWhiteSpace(item.Descripcion))
                foreach (var linea in Wrap("-> " + item.Descripcion.Trim(), 26))
                    parts.Add(EscPos.Line($"        {linea}"));
        }

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
                var desc     = el.TryGetProperty("descripcion", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                list.Add(new Item(nombre, cantidad, precio, desc));
            }
            return list;
        }
        catch { return [new Item("(error leyendo items)", 1, 0, null)]; }
    }

    /// <summary>Corta un texto en líneas de máximo <paramref name="ancho"/> caracteres sin partir palabras.</summary>
    private static IEnumerable<string> Wrap(string texto, int ancho)
    {
        foreach (var parrafo in texto.Replace("\r", "").Split('\n'))
        {
            var linea = "";
            foreach (var palabra in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = palabra.Length > ancho ? palabra[..ancho] : palabra;
                if (linea.Length == 0) linea = p;
                else if (linea.Length + 1 + p.Length <= ancho) linea += " " + p;
                else { yield return linea; linea = p; }
            }
            if (linea.Length > 0) yield return linea;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
