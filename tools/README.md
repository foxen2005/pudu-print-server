# tools/

## SumatraPDF.exe (requerido para imprimir tickets de caja)

El agente imprime el **ticket de caja** (PDF vectorial con timbre TED que genera POS Matic)
usando **SumatraPDF** en modo silencioso (`PdfPrinterService.cs`).

Hay que colocar acá el ejecutable portátil:

1. Descargar **SumatraPDF** (versión *portable*, 64-bit) de https://www.sumatrapdfreader.org/download-free-pdf-viewer
2. Renombrar/copiar el ejecutable como **`SumatraPDF.exe`** dentro de esta carpeta (`tools/SumatraPDF.exe`).
3. El `.csproj` lo copia automáticamente junto al ejecutable del agente al compilar (queda en
   `AppContext.BaseDirectory`, que es donde `PdfPrinterService` lo busca).

Si el archivo no está, el agente compila igual, pero al intentar imprimir un ticket devolverá
`No se encontró SumatraPDF.exe...`.

**Licencia:** SumatraPDF es GPLv3. Se distribuye el binario sin modificar (no se linkea), lo cual
es compatible; incluir la atribución correspondiente en el instalador/About.
