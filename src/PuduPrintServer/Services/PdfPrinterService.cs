using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using PdfiumViewer;

namespace PuduPrintServer.Services;

/// <summary>
/// Imprime un PDF de forma silenciosa renderizándolo con PdfiumViewer e imprimiéndolo por GDI
/// al driver de Windows de la impresora. Se usa para el ticket de caja (PDF vectorial con TED).
///
/// Por qué PdfiumViewer y no SumatraPDF: el print silencioso de SumatraPDF (-print-to -silent) NO
/// genera trabajo de spool en algunos drivers térmicos (verificado en POS-80C: el visor abre, pero
/// no imprime). GDI sí funciona (la página de prueba de Windows sale), y acá imprimimos por GDI.
///
/// Formato/nitidez:
///  - Papel = tamaño real de la página del PDF (no la página larga por defecto del driver) → sin
///    estiramiento ni feed de más. Para facturas largas, el alto sigue al del PDF (rollo continuo);
///    el único tope es el máximo de página del driver.
///  - Se escala al ancho IMPRIMIBLE real y se CENTRA (aire simétrico) → no corta bordes.
///  - Se renderiza a la resolución NATIVA de la impresora y se BINARIZA a blanco/negro puro → sale
///    oscuro y nítido (la térmica dispersa los grises → se veía tenue).
/// </summary>
public class PdfPrinterService
{
    // Umbral de binarizado: luminancia < umbral → negro. Más alto = texto más grueso/oscuro.
    private const int BwThreshold = 190;

    public Task PrintPdfAsync(byte[] pdf, string printerName)
    {
        if (pdf.Length == 0)
            throw new ArgumentException("PDF vacío.");
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("Nombre de impresora vacío.");

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf);
            using var doc = PdfDocument.Load(stream);

            var sizePt = doc.PageSizes[0];               // puntos (1/72")
            double pdfWin = sizePt.Width  / 72.0;        // ancho del PDF en pulgadas
            double pdfHin = sizePt.Height / 72.0;        // alto del PDF en pulgadas

            var paper = new PaperSize("TicketPudu",
                (int)System.Math.Round(pdfWin * 100.0),
                (int)System.Math.Round(pdfHin * 100.0)) { RawKind = (int)PaperKind.Custom };

            using var printDoc = new PrintDocument { DocumentName = "Ticket Pudu" };
            printDoc.PrinterSettings.PrinterName = printerName;
            if (!printDoc.PrinterSettings.IsValid)
                throw new Exception($"Impresora no válida en Windows: {printerName}");

            printDoc.OriginAtMargins = false;
            printDoc.DefaultPageSettings.PaperSize = paper;
            printDoc.DefaultPageSettings.Margins   = new Margins(0, 0, 0, 0);
            printDoc.QueryPageSettings += (_, e) =>
            {
                e.PageSettings.PaperSize = paper;
                e.PageSettings.Margins   = new Margins(0, 0, 0, 0);
            };
            printDoc.PrintController = new StandardPrintController(); // sin diálogo de progreso

            // 1.0.6 (bug 8 del plan 2026-10-20): solo las páginas reales. html2pdf (PDF_generador_.ts
            // del POS) pagina con Math.floor y deja una 2.ª página "fantasma" casi vacía; antes se
            // imprimía igual → un largo de página de papel en blanco por ticket.
            var paginas = PaginasReales(doc);
            int idx = 0;
            printDoc.PrintPage += (_, e) =>
            {
                int page = paginas[idx];
                // Área realmente imprimible (device units = 1/100"), ya sin los márgenes físicos.
                var clip = e.Graphics.VisibleClipBounds;
                // 98% del ancho imprimible → margen simétrico chico + permite centrar.
                float targetW = clip.Width * 0.98f;
                float scale = targetW / (float)(pdfWin * 100.0);
                if (scale > 1f) scale = 1f;              // nunca agrandar de más
                float destW = (float)(pdfWin * 100.0) * scale;
                float destH = (float)(pdfHin * 100.0) * scale;

                // Render a la resolución NATIVA de la impresora (sin re-escalado posterior).
                int dpi = (int)System.Math.Round(e.Graphics.DpiX);
                if (dpi <= 0) dpi = 203;
                int pxW = System.Math.Max(1, (int)System.Math.Round(destW / 100.0 * dpi));
                int pxH = System.Math.Max(1, (int)System.Math.Round(destH / 100.0 * dpi));

                using Image rendered = doc.Render(page, pxW, pxH, dpi, dpi, PdfRenderFlags.ForPrinting);
                using Bitmap bw = ToBlackWhite(rendered, BwThreshold);

                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode   = PixelOffsetMode.Half;
                float x = clip.X + (clip.Width - destW) / 2f;   // centrado horizontal
                e.Graphics.DrawImage(bw, x, clip.Y, destW, destH);

                e.HasMorePages = ++idx < paginas.Count; // 1.0.6: recorre solo las páginas reales
            };

            printDoc.Print();
        });
    }

    /// <summary>
    /// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): el mismo ticket como ESC/POS raster, para
    /// impresoras SIN driver del fabricante (cola "Generic / Text Only" en RAW, red 9100 o Bluetooth COM).
    /// Mismo render que <see cref="PrintPdfAsync"/>: Pdfium dibuja la página directo al ancho en puntos de la
    /// impresora (80 mm = 576, 58 mm = 384, 98 % centrado como en GDI) y se binariza con el mismo umbral. No se
    /// escala después, así que la calidad es la misma que por driver.
    /// </summary>
    public static byte[] RenderRaster(byte[] pdf, int anchoPuntos)
    {
        if (pdf.Length == 0) throw new ArgumentException("PDF vacío.");
        if (anchoPuntos < 200) anchoPuntos = 576;

        using var stream = new MemoryStream(pdf);
        using var doc = PdfDocument.Load(stream);
        using var salida = new MemoryStream();
        salida.Write(EscPos.Initialize());

        foreach (int page in PaginasReales(doc))
        {
            var size = doc.PageSizes[page];
            int pxW = (int)System.Math.Round(anchoPuntos * 0.98);
            int pxH = System.Math.Max(1, (int)System.Math.Round(pxW * size.Height / size.Width));
            // DPI equivalente para que Pdfium no re-escale: puntos / pulgadas de la página.
            int dpi = System.Math.Max(1, (int)System.Math.Round(pxW / (size.Width / 72.0)));

            using Image rendered = doc.Render(page, pxW, pxH, dpi, dpi, PdfRenderFlags.ForPrinting);
            using Bitmap bw = ToBlackWhite(rendered, BwThreshold);
            // Centrado: lienzo del ancho exacto de la impresora con el ticket al medio.
            using var lienzo = new Bitmap(anchoPuntos, pxH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(lienzo))
            {
                g.Clear(Color.White);
                g.DrawImage(bw, new Rectangle((anchoPuntos - pxW) / 2, 0, pxW, pxH), 0, 0, pxW, pxH, GraphicsUnit.Pixel);
            }
            salida.Write(EscPosRaster.Convertir(lienzo));
        }
        return salida.ToArray();
    }

    // 1.0.6: una página final con menos de ~1 mm de alto (o de tinta) es la fantasma de html2pdf.
    // Code review: era 2 mm y podía tirar contenido real (una línea de pie corta en la página 2);
    // con 1 mm solo cae lo que html2pdf deja por redondeo (1-2 px de un borde o de una letra).
    private const double MinAltoPaginaMm = 1.0;
    // DPI de la revisión de contenido. Revisión final: 150 dpi (1 mm ≈ 6 px, suficiente) en vez de
    // 203 para bajar la memoria del render en tickets largos.
    private const int DpiRevision = 150;

    /// <summary>
    /// Índices de las páginas a imprimir (1.0.6). Revisión final: se omiten TODAS las páginas
    /// finales vacías consecutivas (desde la última hacia atrás, nunca la primera) si son fantasma:
    /// o la página mide menos de 1 mm de alto, o —como html2pdf repite el mismo formato en todas las
    /// páginas— su contenido (filas con tinta) mide menos de 1 mm. Cada omisión deja un Info en el log.
    /// </summary>
    private static List<int> PaginasReales(PdfDocument doc)
    {
        int total = doc.PageCount;
        var reales = Enumerable.Range(0, total).ToList();
        while (reales.Count > 1 && EsPaginaFantasma(doc, reales[^1]))
        {
            LogService.Instance.Info("local",
                $"PDF: se omite la página {reales.Count} de {total} (vacía, < {MinAltoPaginaMm} mm de contenido)");
            reales.RemoveAt(reales.Count - 1);
        }
        return reales;
    }

    private static bool EsPaginaFantasma(PdfDocument doc, int page)
    {
        try
        {
            var size = doc.PageSizes[page];
            if (size.Height / 72.0 * 25.4 < MinAltoPaginaMm) return true;

            int pxW = System.Math.Max(1, (int)System.Math.Round(size.Width  / 72.0 * DpiRevision));
            int pxH = System.Math.Max(1, (int)System.Math.Round(size.Height / 72.0 * DpiRevision));
            using Image img = doc.Render(page, pxW, pxH, DpiRevision, DpiRevision, PdfRenderFlags.ForPrinting);
            using Bitmap bw = ToBlackWhite(img, BwThreshold); // mismo umbral que la impresión: "tinta" = lo que saldría negro

            var data = bw.LockBits(new Rectangle(0, 0, bw.Width, bw.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int primera = -1, ultima = -1;
            try
            {
                var buf = new byte[data.Stride * bw.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < bw.Height; y++)
                {
                    int row = y * data.Stride;
                    for (int x = 0; x < bw.Width; x++)
                        if (buf[row + x * 3] == 0) { if (primera < 0) primera = y; ultima = y; break; }
                }
            }
            finally { bw.UnlockBits(data); }

            if (primera < 0) return true; // página en blanco
            double tintaMm = (ultima - primera + 1) / (double)DpiRevision * 25.4;
            return tintaMm < MinAltoPaginaMm;
        }
        catch
        {
            return false; // ante la duda se imprime (mejor papel de más que un ticket incompleto)
        }
    }

    /// <summary>Convierte a blanco/negro puro (umbral de luminancia) para que la térmica imprima
    /// texto/timbre en negro sólido en vez de grises tenues.</summary>
    private static Bitmap ToBlackWhite(Image src, int threshold)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            // Copia PIXEL-POR-PIXEL: DrawImageUnscaled respeta el DPI del render (nativo de la
            // impresora) y encogería el contenido. Con rect explícito en px se copia 1:1.
            g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        int stride = data.Stride;
        int size = stride * bmp.Height;
        var buf = new byte[size];
        Marshal.Copy(data.Scan0, buf, 0, size);
        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = row + x * 3; // BGR
                int lum = (buf[i] * 114 + buf[i + 1] * 587 + buf[i + 2] * 299) / 1000;
                byte v = (byte)(lum < threshold ? 0 : 255);
                buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
        }
        Marshal.Copy(buf, 0, data.Scan0, size);
        bmp.UnlockBits(data);
        return bmp;
    }
}
