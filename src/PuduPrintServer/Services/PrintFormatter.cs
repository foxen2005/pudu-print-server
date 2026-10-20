using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

/// <summary>
/// Arma el papel de la comanda en ESC/POS de texto.
///
/// 1.0.6 — comanda v2 (plan pos-matic_no docs/plans/2026-10-20-rediseno-mesa-gourmet.md, decisión
/// D1 = TEXTO MEJORADO, no imagen): imita el ticket aprobado en maqueta-mesa.html (bloque
/// "Comanda v2", funciones ticket()/ticketTodo() y CSS .p-*):
///
///   COMANDA                       (doble alto + doble ancho)
///   ─ C O C I N A ─               (zona con letras espaciadas)
///   + ADICIONAL #2                (o franja REIMPRESIÓN / bloque ANULAR)
///   - - - - - - - - - - - - -
///   MESA                 S.8      (doble alto + doble ancho)
///   Garzón / Comensales / Fecha/hora a dos columnas
///   ┌ * NOTA … ┐                  (recuadro, texto en negrita)
///   2× NOMBRE EN MAYÚSCULAS   √   (cantidad doble alto; √ = ya en producción)
///      -> descripción
///   TOTAL ÍTEMS: n
///   Documento interno · solo producción
///
/// Ancho: 48 columnas (fuente A, 80 mm) por defecto; 32 para 58 mm (config `ancho_columnas`).
/// </summary>
public class PrintFormatter
{
    /// <summary>Columnas de fuente A en papel de 80 mm (576 puntos / 12). 58 mm = 32 (1.0.6).</summary>
    public const int AnchoPorDefecto = 48;

    // 1.0.6: Cantidad pasa de int a decimal (bug 7: "1.5" kg tiraba la comanda entera con GetInt32).
    // Se quita PrecioVenta (cocina no lo imprime). YaEnviado: null = el POS no mandó el dato.
    private record Item(string Nombre, decimal Cantidad, string? Descripcion, bool? YaEnviado, bool Ilegible = false);

    // 1.0.6: el POS anterior a la comanda v2 manda el rótulo pegado al nombre de la zona
    // ("Cocina · ADICIONAL #2", "Cocina · ANULAR", "Cocina · REIMPRESIÓN – NO PREPARAR DE NUEVO",
    // comandaZonas.ts:234-243) en vez de tipo_comanda/secuencia. Se separa para dibujar la zona
    // limpia y el rótulo con el diseño nuevo.
    private static readonly Regex RotuloEnZona = new(
        @"^(?<zona>.*?)\s*·\s*(?:(?<adic>ADICIONAL)\s*#\s*(?<sec>\d+)|(?<anul>ANULAR)|(?<reimp>REIMPRESI[ÓO]N).*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public byte[] FormatComanda(ColaImpresionJob job, string negocioNombre, int anchoColumnas = AnchoPorDefecto)
    {
        // 1.0.6: el diseño aprobado ya no lleva el nombre del negocio arriba (título "COMANDA");
        // se conserva el parámetro para no cambiar la firma que usa PrintQueueProcessor.
        _ = negocioNombre;

        // Topes de sanidad: menos de 24 columnas no entra ni "Fecha/hora", más de 64 no existe en térmicas.
        int w = Math.Clamp(anchoColumnas, 24, 64);
        var ahora = DateTime.Now;

        // ── Zona, tipo y secuencia ────────────────────────────────────────────────────────────
        var zonaCruda = job.ZonaNombre ?? job.ZonaId[..Math.Min(8, job.ZonaId.Length)];
        var tipoExplicito = NormalizarTipo(job.TipoComanda);
        string zona;
        string? tipo;
        int? secuencia;
        if (tipoExplicito is not null)
        {
            // Contrato POS → agente 1.0.6: con tipoComanda, zonaNombre SIGUE trayendo el rótulo viejo
            // pegado ("Barra · ADICIONAL #2", para que el agente 1.0.5 lo muestre). La zona limpia
            // es zonaBase; si falta, la parte de zonaNombre antes de " · ". El rótulo se arma SOLO
            // desde tipoComanda + secuencia (no desde el texto pegado).
            zona = !string.IsNullOrWhiteSpace(job.ZonaBase)
                ? job.ZonaBase.Trim()
                : AntesDelSeparador(zonaCruda);
            tipo = tipoExplicito;
            secuencia = job.Secuencia;
        }
        else
        {
            // POS viejo (sin tipoComanda): se deduce el rótulo del sufijo pegado a la zona, si lo hay.
            (zona, tipo, secuencia) = SepararRotulo(zonaCruda);
            secuencia = job.Secuencia ?? secuencia;
        }

        bool anulacion   = tipo == "anulacion";
        bool reimpresion = tipo == "reimpresion";
        // "+ ADICIONAL #n" solo si el tipo es adicional (la secuencia ahora es por zona y la
        // reimpresión/marcado no consumen número: una secuencia > 1 sola no significa adicional).
        bool adicional   = tipo == "adicional";

        var items = ParseItems(job.ItemsJson);

        // Anulación: cantidades SIEMPRE negativas en el papel (el POS ya las manda así, pero si
        // llegaran positivas igual se ve que es para descontar).
        if (anulacion)
            items = items.Select(i => i.Cantidad > 0 ? i with { Cantidad = -i.Cantidad } : i).ToList();

        // Reimpresión sin el dato ya_enviado (POS viejo): todo lo que se reimprime ya estaba en
        // producción → check en todo y "No preparar de nuevo" (mismo mensaje que la 1.0.5).
        if (reimpresion && items.All(i => i.YaEnviado is null))
            items = items.Select(i => i with { YaEnviado = true }).ToList();

        var p = new List<byte[]>
        {
            EscPos.Initialize(),
            EscPos.CodeTableCp850(), // 1.0.6: tabla explícita (ver EscPos.CodeTableCp850)
            EscPos.Left(),
        };

        // ── Título ─────────────────────────────────────────────────────────────────────────────
        p.Add(EscPos.Center());
        p.Add(EscPos.BoldOn());
        p.Add(EscPos.Size(dobleAncho: true, dobleAlto: true));
        p.Add(EscPos.Line("COMANDA"));
        p.Add(EscPos.SizeNormal());

        // ── Zona con letras espaciadas: "─ C O C I N A ─" ──────────────────────────────────────
        foreach (var linea in LineaZona(zona.ToUpperInvariant(), w))
            p.Add(EscPos.Line(linea));
        p.Add(EscPos.BoldOff());

        // ── Rótulo: ANULAR / REIMPRESIÓN / + ADICIONAL #n ──────────────────────────────────────
        if (anulacion)
        {
            // Franja liviana entre dos líneas finas, igual que la reimpresión (Jean: sin bloques
            // negros, "muy pesado"). "ANULAR" en doble ancho + negrita; aclaración debajo.
            p.Add(EscPos.Line(new string('─', w)));
            p.Add(EscPos.BoldOn());
            p.Add(EscPos.Size(dobleAncho: true, dobleAlto: false));
            p.Add(EscPos.Line("ANULAR"));          // doble ancho: se ve "A N U L A R"
            p.Add(EscPos.SizeNormal());
            p.Add(EscPos.BoldOff());
            p.Add(EscPos.Line("No preparar lo siguiente"));
            p.Add(EscPos.Line(new string('─', w)));
        }
        else if (reimpresion)
        {
            // Franja entre dos líneas (CSS .p-band): título espaciado + aclaración.
            bool todoEnviado = items.Count > 0 && items.All(i => i.YaEnviado == true);
            p.Add(EscPos.Line(new string('─', w)));
            p.Add(EscPos.BoldOn());
            p.Add(EscPos.Line(Espaciar("REIMPRESIÓN", w)));
            p.Add(EscPos.BoldOff());
            if (todoEnviado)
                p.Add(EscPos.Line("No preparar de nuevo"));
            else
            {
                // Mezcla de lo nuevo con lo ya enviado: el √ dice qué ya está hecho.
                p.Add(EscPos.Check());
                p.Add(EscPos.Line(" = ya en producción"));
            }
            p.Add(EscPos.Line(new string('─', w)));
        }
        else if (adicional)
        {
            p.Add(EscPos.BoldOn());
            p.Add(EscPos.Line(secuencia is > 0 ? $"+ ADICIONAL #{secuencia}" : "+ ADICIONAL"));
            p.Add(EscPos.BoldOff());
        }

        p.Add(EscPos.Left());
        // Las franjas de reimpresión y anulación ya cierran con una línea: no repetir la punteada.
        if (!reimpresion && !anulacion)
            p.Add(EscPos.Line(new string('-', w)));

        // ── MESA grande (doble ancho → la línea tiene w/2 columnas) ────────────────────────────
        if (!string.IsNullOrWhiteSpace(job.MesaNumero))
        {
            int wGrande = w / 2;
            var mesa = job.MesaNumero.Trim().ToUpperInvariant();
            p.Add(EscPos.BoldOn());
            p.Add(EscPos.Size(dobleAncho: true, dobleAlto: true));
            if ("MESA".Length + 1 + mesa.Length <= wGrande)
                p.Add(EscPos.Line(DosColumnas("MESA", mesa, wGrande)));
            else
            {
                // Nombre de mesa largo ("TERRAZA FONDO 12"): en su propia línea, sin cortar.
                p.Add(EscPos.Line("MESA"));
                foreach (var l in Wrap(mesa, wGrande))
                    p.Add(EscPos.Line(l.PadLeft(wGrande)));
            }
            p.Add(EscPos.SizeNormal());
            p.Add(EscPos.BoldOff());
        }

        // ── Garzón / Comensales / Fecha a dos columnas (valor a la derecha, en negrita) ────────
        if (!string.IsNullOrWhiteSpace(job.GarzonNombre))
            p.AddRange(FilaDato("Garzón", job.GarzonNombre.Trim(), w, negrita: true));
        if (job.Comensales is > 0)
            p.AddRange(FilaDato("Comensales", job.Comensales.Value.ToString(CultureInfo.InvariantCulture), w, negrita: true));
        p.AddRange(FilaDato("Fecha/hora", ahora.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture), w, negrita: false));

        // ── Nota del pedido en recuadro (antes de los productos: cocina la lee primero) ─────────
        if (!string.IsNullOrWhiteSpace(job.Notas))
            p.AddRange(RecuadroNota(job.Notas.Trim(), w));

        // ── Ítems ──────────────────────────────────────────────────────────────────────────────
        p.Add(EscPos.FeedDots(10));
        if (items.Count == 0)
        {
            p.Add(EscPos.Center());
            p.Add(EscPos.Line("Nada para esta zona"));
            p.Add(EscPos.Left());
        }
        else
        {
            // Columna de cantidad del ancho de la cantidad más larga ("0.25", "-2") → nombres alineados.
            int anchoCant = Math.Max(2, items.Max(i => FormatCantidad(i.Cantidad).Length));
            for (int k = 0; k < items.Count; k++)
            {
                p.AddRange(LineasItem(items[k], anchoCant, w));
                if (k < items.Count - 1)
                    p.Add(EscPos.Line(new string('·', w))); // separador fino (CSS .p-item border dashed)
            }
        }

        // ── Total, pie y corte ─────────────────────────────────────────────────────────────────
        p.Add(EscPos.FeedDots(10));
        p.Add(EscPos.Center());
        p.Add(EscPos.BoldOn());
        p.Add(EscPos.Line($"TOTAL ÍTEMS: {FormatCantidad(items.Where(i => !i.Ilegible).Sum(i => i.Cantidad))}"));
        p.Add(EscPos.BoldOff());
        p.Add(EscPos.Left());
        p.Add(EscPos.Line(new string('-', w)));
        p.Add(EscPos.Center());
        p.Add(EscPos.Line(w >= 35 ? "Documento interno · solo producción" : "Doc. interno · solo producción"));
        p.Add(EscPos.Left());

        // 1.0.6: antes Feed(4) + GS V 65 3 (4 renglones fijos + distancia a la cuchilla ≈ 2 cm en
        // blanco). GS V 66 0 avanza solo hasta la cuchilla; se deja UN renglón para que el corte no
        // roce el pie (la cuchilla corta justo bajo la posición de impresión actual).
        p.Add(EscPos.Feed(1));
        p.Add(EscPos.CutFeed());

        return EscPos.Concat(p.ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Bloques del papel
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Una línea de ítem: "2× NOMBRE" con la cantidad en doble alto y negrita, nombre en negrita y
    /// mayúsculas que salta de línea si no cabe (no se trunca), √ al final si ya estaba en
    /// producción, y "-> descripción" debajo alineada con el nombre.
    /// </summary>
    private static IEnumerable<byte[]> LineasItem(Item item, int anchoCant, int w)
    {
        var cant = item.Ilegible ? "?" : FormatCantidad(item.Cantidad);
        var prefijo = cant.PadLeft(anchoCant) + "× ";   // "× " = ×(0x9E en CP850) + espacio
        int sangria = prefijo.Length;
        bool check = item.YaEnviado == true;
        // Con √ se reservan 2 columnas al final (" √") para que el check quede siempre en el borde.
        int anchoNombre = Math.Max(8, w - sangria - (check ? 2 : 0));

        var lineasNombre = Wrap(item.Nombre.ToUpperInvariant(), anchoNombre).ToList();
        if (lineasNombre.Count == 0) lineasNombre.Add("");

        // Primera línea: cantidad en doble alto (doble alto NO cambia el ancho de columna, así la
        // alineación se mantiene), luego el nombre a tamaño normal; el renglón toma el alto mayor.
        yield return EscPos.BoldOn();
        yield return EscPos.Size(dobleAncho: false, dobleAlto: true);
        yield return EscPos.Text(prefijo);
        yield return EscPos.SizeNormal();
        if (check)
        {
            yield return EscPos.Text(lineasNombre[0].PadRight(anchoNombre) + " ");
            yield return EscPos.Check();
            yield return EscPos.Line();
        }
        else
            yield return EscPos.Line(lineasNombre[0]);

        // Resto del nombre: sangrado bajo el nombre, también en negrita.
        foreach (var l in lineasNombre.Skip(1))
            yield return EscPos.Line(new string(' ', sangria) + l);
        yield return EscPos.BoldOff();

        // Nota por producto ("sin cebolla"), normal, bajo el nombre.
        if (!string.IsNullOrWhiteSpace(item.Descripcion))
            foreach (var l in Wrap("-> " + item.Descripcion.Trim(), Math.Max(8, w - sangria)))
                yield return EscPos.Line(new string(' ', sangria) + l);
    }

    /// <summary>
    /// Recuadro de la nota del pedido con caracteres de caja de CP850 (┌─┐│└┘): "* NOTA" y el texto
    /// en negrita; el marco va normal para que no se vea como una mancha.
    /// </summary>
    private static IEnumerable<byte[]> RecuadroNota(string nota, int w)
    {
        int interior = w - 4; // "│ " + texto + " │"
        yield return EscPos.FeedDots(6);
        yield return EscPos.Line("┌" + new string('─', w - 2) + "┐");
        foreach (var (texto, i) in new[] { "* NOTA" }.Concat(Wrap(nota, interior)).Select((t, i) => (t, i)))
        {
            yield return EscPos.Text("│ ");
            yield return EscPos.BoldOn();
            yield return EscPos.Text(texto.PadRight(interior));
            yield return EscPos.BoldOff();
            yield return EscPos.Line(" │");
        }
        yield return EscPos.Line("└" + new string('─', w - 2) + "┘");
    }

    /// <summary>"Garzón ........ raul": etiqueta a la izquierda, valor a la derecha. Si no caben juntos, el valor baja.</summary>
    private static IEnumerable<byte[]> FilaDato(string etiqueta, string valor, int w, bool negrita)
    {
        if (etiqueta.Length + 1 + valor.Length <= w)
        {
            yield return EscPos.Text(etiqueta.PadRight(w - valor.Length));
            if (negrita) yield return EscPos.BoldOn();
            yield return EscPos.Line(valor);
            if (negrita) yield return EscPos.BoldOff();
            yield break;
        }
        yield return EscPos.Line(etiqueta);
        if (negrita) yield return EscPos.BoldOn();
        foreach (var l in Wrap(valor, w))
            yield return EscPos.Line(l.PadLeft(w));
        if (negrita) yield return EscPos.BoldOff();
    }

    /// <summary>
    /// "─ C O C I N A ─" centrado (la alineación la pone el llamador). Si espaciada no cabe, va sin
    /// espaciar; si tampoco, salta de línea (nunca se corta el nombre de la zona).
    /// </summary>
    private static IEnumerable<string> LineaZona(string zona, int w)
    {
        var espaciada = string.Join(" ", zona.Select(c => c == ' ' ? " " : c.ToString())); // "BARRA FRIA" → "B A R R A   F R I A"
        if (espaciada.Length + 4 <= w) return [$"─ {espaciada} ─"];
        if (zona.Length + 4 <= w) return [$"─ {zona} ─"];
        return Wrap(zona, w);
    }

    /// <summary>Texto con letras espaciadas si cabe en <paramref name="w"/>; si no, tal cual.</summary>
    private static string Espaciar(string s, int w)
    {
        var e = string.Join(" ", s.ToCharArray());
        return e.Length <= w ? e : s;
    }

    private static string DosColumnas(string izq, string der, int w) =>
        izq + new string(' ', Math.Max(1, w - izq.Length - der.Length)) + der;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Datos
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Separa "Cocina · ADICIONAL #2" en ("Cocina", "adicional", 2). Sin rótulo devuelve el nombre
    /// tal cual y tipo/secuencia null (1.0.6, compatibilidad con el POS que pega el rótulo a la zona).
    /// </summary>
    private static (string zona, string? tipo, int? secuencia) SepararRotulo(string zonaNombre)
    {
        var m = RotuloEnZona.Match(zonaNombre.Trim());
        if (!m.Success || m.Groups["zona"].Value.Length == 0) return (zonaNombre.Trim(), null, null);
        var zona = m.Groups["zona"].Value;
        if (m.Groups["adic"].Success)
            return (zona, "adicional", int.TryParse(m.Groups["sec"].Value, out var s) ? s : null);
        if (m.Groups["anul"].Success)  return (zona, "anulacion", null);
        if (m.Groups["reimp"].Success) return (zona, "reimpresion", null);
        return (zonaNombre.Trim(), null, null);
    }

    /// <summary>"Barra · ADICIONAL #2" → "Barra" (todo lo anterior al primer " · "); sin separador, tal cual (1.0.6).</summary>
    private static string AntesDelSeparador(string zonaNombre)
    {
        var i = zonaNombre.IndexOf('·');
        var limpia = (i > 0 ? zonaNombre[..i] : zonaNombre).Trim();
        return limpia.Length > 0 ? limpia : zonaNombre.Trim();
    }

    /// <summary>tipo_comanda en minúsculas y sin tilde ("Anulación" → "anulacion"); vacío → null.</summary>
    private static string? NormalizarTipo(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return null;
        return tipo.Trim().ToLowerInvariant()
            .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');
    }

    /// <summary>
    /// Lee los ítems del CartItem[] del POS. 1.0.6: cada ítem va en su propio try → un ítem malo
    /// sale como "(error leyendo ítem)" y el resto de la comanda se imprime igual (antes una
    /// cantidad decimal tiraba la lista entera, bug 7).
    /// </summary>
    private static List<Item> ParseItems(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return [new Item("(error leyendo ítems)", 1, null, null, Ilegible: true)]; }

        using (doc)
        {
            var root = doc.RootElement;
            // `items` puede llegar como string JSON (cola_impresion guarda JSON.stringify): desenvolver.
            if (root.ValueKind == JsonValueKind.String)
            {
                try { return ParseItems(root.GetString() ?? "[]"); }
                catch { return [new Item("(error leyendo ítems)", 1, null, null, Ilegible: true)]; }
            }
            if (root.ValueKind != JsonValueKind.Array)
                return [new Item("(error leyendo ítems)", 1, null, null, Ilegible: true)];

            var list = new List<Item>();
            foreach (var el in root.EnumerateArray())
            {
                try
                {
                    // Campos reales del CartItem de POS Matic:
                    // "quantity" (no "cantidad"), "descripcion" = nota por producto.
                    var nombre   = el.TryGetProperty("nombre", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    var cantidad = el.TryGetProperty("quantity", out var c) ? LeerCantidad(c) : 1m;
                    var desc     = el.TryGetProperty("descripcion", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    var yaEnv    = LeerYaEnviado(el);
                    list.Add(new Item(nombre, cantidad, desc, yaEnv));
                }
                catch
                {
                    list.Add(new Item("(error leyendo ítem)", 0, null, null, Ilegible: true));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Cantidad como decimal (1.0.6, bug 7): número JSON (2, 1.5, 0.25, -2) o string parseable
    /// ("1.5" o "1,5"). Cualquier otra cosa lanza → el ítem sale como ilegible, no la comanda entera.
    /// </summary>
    private static decimal LeerCantidad(JsonElement c)
    {
        switch (c.ValueKind)
        {
            case JsonValueKind.Number:
                return c.GetDecimal();
            case JsonValueKind.String:
                var s = (c.GetString() ?? "").Trim().Replace(',', '.');
                return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.Null:
                return 1m;
            default:
                throw new FormatException($"quantity inválida: {c.ValueKind}");
        }
    }

    /// <summary>
    /// `ya_enviado` por ítem (1.0.6, para el √): bool, o número = cantidad previamente enviada
    /// (el plan Fase 2 la describe así; > 0 cuenta como ya en producción). Acepta también
    /// `yaEnviado`. Ausente o null → null (el POS no mandó el dato).
    /// </summary>
    private static bool? LeerYaEnviado(JsonElement el)
    {
        if (!el.TryGetProperty("ya_enviado", out var v) && !el.TryGetProperty("yaEnviado", out v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Number => v.TryGetDecimal(out var q) && q > 0,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b
                                   : decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q2) && q2 > 0,
            _ => null,
        };
    }

    /// <summary>"2", "1.5", "0.25", "-2": sin ceros sobrantes, punto decimal (1.0.6).</summary>
    private static string FormatCantidad(decimal q) =>
        q.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Corta un texto en líneas de máximo <paramref name="ancho"/> caracteres sin partir palabras.
    /// 1.0.6: una palabra más larga que el ancho se PARTE en trozos (antes se truncaba y el nombre
    /// del producto se perdía).
    /// </summary>
    private static IEnumerable<string> Wrap(string texto, int ancho)
    {
        foreach (var parrafo in texto.Replace("\r", "").Split('\n'))
        {
            var linea = "";
            foreach (var palabraEntera in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var palabra = palabraEntera;
                // Palabra larguísima: llenar lo que quede de la línea actual y seguir en la siguiente.
                while (palabra.Length > ancho)
                {
                    if (linea.Length > 0) { yield return linea; linea = ""; }
                    yield return palabra[..ancho];
                    palabra = palabra[ancho..];
                }
                if (palabra.Length == 0) continue;
                if (linea.Length == 0) linea = palabra;
                else if (linea.Length + 1 + palabra.Length <= ancho) linea += " " + palabra;
                else { yield return linea; linea = palabra; }
            }
            if (linea.Length > 0) yield return linea;
        }
    }
}
