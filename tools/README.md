# tools/

## Impresión de tickets de caja — sin binarios externos

El ticket de caja (PDF con timbre TED que genera POS Matic) se imprime **dentro del agente**
usando **PdfiumViewer** (`Services/PdfPrinterService.cs`): renderiza el PDF y lo imprime por GDI
al driver de Windows de la impresora. El motor nativo `pdfium.dll` llega vía NuGet
(`PdfiumViewer.Native.x86_64.v8-xfa`) — **no hace falta colocar ningún .exe acá**.

> **Nota histórica:** se probó SumatraPDF (`SumatraPDF.exe`), pero su print silencioso
> (`-print-to -silent`) no genera trabajo de spool en varios drivers térmicos (verificado en
> POS-80C: el visor abre, pero no imprime). Se descartó a favor de PdfiumViewer (impresión GDI,
> que sí funciona en esos drivers). El `.gitignore` de esta carpeta ignora cualquier `*.exe`
> residual de esa prueba.
