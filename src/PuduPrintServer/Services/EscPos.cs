using System.Text;

namespace PuduPrintServer.Services;

public static class EscPos
{
    private static readonly Encoding Cp850 = Encoding.GetEncoding(850);

    /// <summary>
    /// ESC @ (reinicia la impresora) + ESC t 2 (tabla PC850).
    ///
    /// 1.0.6 (code review): la selección de PC850 vive acá y no en cada formato, así TODO lo que
    /// manda el agente por ESC/POS (comanda y tickets de prueba de PrinterService) queda en la misma
    /// tabla con que se codifica el texto (<see cref="Text"/>). Hasta la 1.0.5 solo se mandaba ESC @,
    /// que deja la tabla de fábrica (casi siempre PC437): las minúsculas acentuadas y la Ñ coinciden
    /// en ambas, pero las MAYÚSCULAS acentuadas (Á Í Ó Ú) y el "×" no, y la comanda v2 imprime los
    /// nombres en mayúsculas. ESC t 2 = PC850 en la numeración Epson (la que siguen las térmicas chinas).
    /// </summary>
    public static byte[] Initialize() => [0x1B, 0x40, 0x1B, 0x74, 0x02];
    public static byte[] BoldOn()     => [0x1B, 0x45, 0x01];
    public static byte[] BoldOff()    => [0x1B, 0x45, 0x00];
    public static byte[] Center()     => [0x1B, 0x61, 0x01];
    public static byte[] Left()       => [0x1B, 0x61, 0x00];
    /// <summary>
    /// GS V 65 3 — corte completo "función B": avanza hasta la cuchilla (+3 unidades) y corta.
    /// 1.0.6 (revisión final): es el MISMO comando que la 1.0.5 ya usaba en los locales (probado en
    /// sus impresoras), ahora SIN el Feed(4) previo, que era el que gastaba ~2 cm de papel por
    /// comanda. Se descartó GS V 65 0 / 66 0: algunos clones interpretan n=0 como "cortar sin
    /// avanzar" y cortan las últimas líneas. También lo usa el corte RAW después de un PDF.
    /// </summary>
    public static byte[] Cut()        => [0x1D, 0x56, 0x41, 0x03];
    public static byte[] Feed(int n = 1) => Enumerable.Repeat((byte)0x0A, n).ToArray();
    public static byte[] Text(string s)  => Cp850.GetBytes(s);
    public static byte[] Line(string s = "") => Cp850.GetBytes(s + "\n");

    // 1.0.6 (code review): se borraron BigOn/BigOff (ESC !, reemplazados por Size) y las primitivas
    // que se agregaron sin uso (Right, UnderlineOn/Off, LineSpacing, LineSpacingDefault, CodeTable,
    // CodeTableCp850, ReverseOn/Off). Se agregan cuando algo las necesite.

    /// <summary>
    /// Texto tal como lo va a imprimir CP850, para calcular anchos por BYTE y no por char UTF-16
    /// (1.0.6, code review). Un emoji son 2 chars UTF-16 (surrogate) pero sale como UN "?" de una
    /// columna; un acento combinante (e + U+0301) se recompone primero (NFC) para que salga "é".
    /// Los tabs pasan a espacio (en la térmica saltan a un tab stop y descuadran). Después de esto,
    /// Length == bytes impresos == columnas, y los PadLeft/PadRight del recuadro y las dos columnas
    /// cuadran.
    /// </summary>
    public static string Imprimible(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 1.0.6 (revisión final): primero se sanean los surrogates, ANTES de Normalize, porque
        // string.Normalize LANZA con un surrogate suelto (texto mal cortado en el POS) y eso tiraba
        // la comanda entera. Par válido (emoji, fuera del plano básico) → un solo "?" (el encoder de
        // CP850 pondría "??", uno por mitad); surrogate sin pareja → "?". Tab → espacio.
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            { sb.Append('?'); i++; }
            else if (char.IsSurrogate(c))
                sb.Append('?');
            else
                sb.Append(c == '\t' ? ' ' : c);
        }
        var nfc = sb.ToString().Normalize(NormalizationForm.FormC);
        return Cp850.GetString(Cp850.GetBytes(nfc));
    }

    /// <summary>
    /// GS ! n — tamaño de carácter (1.0.6). Doble ancho = bit 4 (0x10), doble alto = bit 0 (0x01).
    /// Se usa GS ! (y no ESC !) porque no toca la negrita ni la fuente: la negrita se controla
    /// aparte con ESC E. Ojo: con doble ANCHO la línea tiene la mitad de columnas; con doble ALTO
    /// solo, las columnas no cambian (por eso la cantidad del ítem usa solo doble alto).
    /// </summary>
    public static byte[] Size(bool dobleAncho, bool dobleAlto) =>
        [0x1D, 0x21, (byte)((dobleAncho ? 0x10 : 0x00) | (dobleAlto ? 0x01 : 0x00))];

    /// <summary>GS ! 0 — vuelve al tamaño normal (1.0.6).</summary>
    public static byte[] SizeNormal() => [0x1D, 0x21, 0x00];

    /// <summary>
    /// ESC J n — avanza n puntos sin imprimir (1.0.6). Sirve para separar bloques con medio
    /// renglón en vez de una línea en blanco entera → ahorra papel.
    /// </summary>
    public static byte[] FeedDots(byte puntos) => [0x1B, 0x4A, puntos];

    /// <summary>
    /// Carácter √ (check de "ya en producción", 1.0.6). CP850 no lo tiene; PC437 sí (0xFB).
    /// ESC t 0 (PC437) solo para ese byte y ESC t 2 para volver a PC850, la tabla que fija
    /// <see cref="Initialize"/>.
    /// </summary>
    public static byte[] Check() => [0x1B, 0x74, 0x00, 0xFB, 0x1B, 0x74, 0x02];

    public static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();
}
