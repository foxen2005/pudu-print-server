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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1.0.6 — primitivas nuevas para la comanda v2 (diseño aprobado en maqueta-mesa.html).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ESC a 2 — alinear a la derecha (1.0.6).</summary>
    public static byte[] Right() => [0x1B, 0x61, 0x02];

    /// <summary>
    /// GS ! n — tamaño de carácter (1.0.6). Doble ancho = bit 4 (0x10), doble alto = bit 0 (0x01).
    /// Se usa GS ! (y no ESC ! como BigOn) porque no toca la negrita ni la fuente: la negrita se
    /// controla aparte con ESC E. Ojo: con doble ANCHO la línea tiene la mitad de columnas; con
    /// doble ALTO solo, las columnas no cambian (por eso la cantidad del ítem usa solo doble alto).
    /// </summary>
    public static byte[] Size(bool dobleAncho, bool dobleAlto) =>
        [0x1D, 0x21, (byte)((dobleAncho ? 0x10 : 0x00) | (dobleAlto ? 0x01 : 0x00))];

    /// <summary>GS ! 0 — vuelve al tamaño normal (1.0.6).</summary>
    public static byte[] SizeNormal() => [0x1D, 0x21, 0x00];

    /// <summary>ESC - n — subrayado 1 punto / sin subrayado (1.0.6).</summary>
    public static byte[] UnderlineOn()  => [0x1B, 0x2D, 0x01];
    public static byte[] UnderlineOff() => [0x1B, 0x2D, 0x00];

    /// <summary>ESC 3 n — interlineado de n puntos (1.0.6). A 203 dpi, 30 puntos ≈ 3,75 mm (el default).</summary>
    public static byte[] LineSpacing(byte puntos) => [0x1B, 0x33, puntos];

    /// <summary>ESC 2 — interlineado por defecto de la impresora (1.0.6).</summary>
    public static byte[] LineSpacingDefault() => [0x1B, 0x32];

    /// <summary>
    /// ESC J n — avanza n puntos sin imprimir (1.0.6). Sirve para separar bloques con medio
    /// renglón en vez de una línea en blanco entera → ahorra papel.
    /// </summary>
    public static byte[] FeedDots(byte puntos) => [0x1B, 0x4A, puntos];

    /// <summary>ESC t n — tabla de caracteres (1.0.6). 0 = PC437, 2 = PC850 (numeración Epson).</summary>
    public static byte[] CodeTable(byte n) => [0x1B, 0x74, n];

    /// <summary>
    /// ESC t 2 — selecciona PC850 (1.0.6). El texto SIEMPRE se codifica en CP850 (ver <see cref="Text"/>),
    /// pero hasta la 1.0.5 nunca se le decía a la impresora qué tabla usar: ESC @ la deja en su
    /// default de fábrica (casi siempre PC437). Las minúsculas acentuadas y la Ñ coinciden en ambas
    /// tablas, pero las MAYÚSCULAS acentuadas (Á Í Ó Ú) y el "×" no: como la comanda v2 imprime los
    /// nombres en mayúsculas, ahora se fija la tabla explícitamente después de cada ESC @.
    /// </summary>
    public static byte[] CodeTableCp850() => CodeTable(2);

    /// <summary>
    /// Carácter √ (check de "ya en producción", 1.0.6). CP850 no lo tiene; PC437 sí (0xFB).
    /// Se cambia a PC437 solo para ese byte y se vuelve a PC850, que es la tabla que fija la
    /// comanda al inicializar (<see cref="CodeTableCp850"/>).
    /// </summary>
    public static byte[] Check() => [0x1B, 0x74, 0x00, 0xFB, 0x1B, 0x74, 0x02];

    /// <summary>
    /// GS V 66 0 — corte "función B" (1.0.6): la impresora AVANZA SOLA el papel hasta que la última
    /// línea impresa pasa la cuchilla y recién ahí corta (corte parcial, deja una "bisagra" mínima;
    /// en la mayoría de las térmicas chinas actúa como corte total). Reemplaza Feed(4) + GS V 65 3,
    /// que avanzaba 4 renglones fijos MÁS la distancia cabezal→cuchilla → ~2 cm de papel en blanco
    /// por comanda. No necesita feed previo: el avance hasta la cuchilla lo calcula la impresora.
    /// </summary>
    public static byte[] CutFeed() => [0x1D, 0x56, 0x42, 0x00];

    public static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();
}
