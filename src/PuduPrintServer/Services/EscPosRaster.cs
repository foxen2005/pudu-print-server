using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PuduPrintServer.Services;

/// <summary>
/// 1.0.7 (docs/plans/2026-11-13-impresoras-genericas-sin-driver.md): imagen en blanco y negro → ESC/POS raster
/// (GS v 0). Es lo que permite imprimir el ticket PDF en una térmica SIN driver del fabricante (red 9100, Bluetooth
/// COM o una cola "Generic / Text Only" en RAW). Mismos píxeles que el camino por driver: el bitmap ya viene al
/// ancho en puntos de la impresora, acá no se escala nada.
/// </summary>
public static class EscPosRaster
{
    /// <summary>Alto de cada franja: varias impresoras chinas tienen buffer chico y cortan imágenes largas.</summary>
    private const int AltoFranja = 128;

    /// <summary>
    /// Convierte un bitmap (cualquier formato; negro = píxel con luminancia &lt; 128) a GS v 0 en franjas.
    /// El ancho se completa a múltiplo de 8 con blanco.
    /// </summary>
    public static byte[] Convertir(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int bytesFila = (w + 7) / 8;

        using var ms = new MemoryStream();
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var fila = new byte[Math.Abs(stride)];
            for (int y0 = 0; y0 < h; y0 += AltoFranja)
            {
                int alto = Math.Min(AltoFranja, h - y0);
                // GS v 0 m xL xH yL yH
                ms.Write([0x1D, 0x76, 0x30, 0x00,
                          (byte)(bytesFila & 0xFF), (byte)(bytesFila >> 8),
                          (byte)(alto & 0xFF), (byte)(alto >> 8)]);
                var salida = new byte[bytesFila];
                for (int y = y0; y < y0 + alto; y++)
                {
                    Marshal.Copy(data.Scan0 + y * stride, fila, 0, fila.Length);
                    Array.Clear(salida);
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 3;
                        // BGR → luminancia
                        int lum = (fila[i + 2] * 299 + fila[i + 1] * 587 + fila[i] * 114) / 1000;
                        if (lum < 128) salida[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                    ms.Write(salida);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return ms.ToArray();
    }
}
