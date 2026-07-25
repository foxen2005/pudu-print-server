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

            int page = 0;
            printDoc.PrintPage += (_, e) =>
            {
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

                e.HasMorePages = ++page < doc.PageCount;
            };

            printDoc.Print();
        });
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
