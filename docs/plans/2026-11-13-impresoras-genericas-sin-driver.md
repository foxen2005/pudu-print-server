# Impresoras térmicas genéricas sin driver (red, USB y Bluetooth) — plan 2026-11-13

> Estado: **PLAN (sin implementar)**. Pedido de Jean: "nuestro pudu print server no añade impresoras no
> instaladas, debemos mejorar eso, que instale también estas impresoras genéricas por red, usb y bluetooth,
> que incluya algunos drivers".

## 1. El caso

Térmica de red genérica en casa de Jean: `192.168.1.18`, puerto **9100 abierto** (RAW), web de configuración
"Ethernet WebSet System" (GB2312, típica Xprinter / POS-80). Probado 2026-11-13: un ticket ESC/POS mandado por
socket a 9100 imprimió y cortó (visto por cámara). Windows no tiene driver térmico instalado; se creó solo el
puerto `IP_192.168.1.18`.

## 2. Por qué hoy no sirve en el agente

- `ImpresoraLocal.TipoConexion` es solo `WIN | COM` (el README habla de TCP/IP pero el modelo no lo tiene).
- El **ticket de caja** (`/imprimir`, PDF con TED) va por `PdfPrinterService` → `PrintDocument` → GDI → **driver de
  Windows**. Sin driver del fabricante no hay cola, y con "Generic / Text Only" (el único de Windows) el GDI no
  dibuja imágenes: el timbre y el logo no salen.
- `/impresoras` solo lista colas de Windows: una impresora de red o USB sin driver no aparece en POS Matic.

## 3. Propuesta: imprimir sin driver (ESC/POS raster) + asistente "Agregar impresora"

### 3.1 Ticket PDF como imagen ESC/POS (el cambio de fondo)
- Renderizar cada página con Pdfium (ya está) al ancho de la impresora en puntos (80 mm → 576 px, 58 mm → 384 px
  a 203 dpi), pasar a blanco y negro con el mismo umbral (`ToBlackWhite` ya existe) y mandarla como raster
  **GS v 0** en franjas de 24–255 líneas, más el corte que ya existe (`cortar_despues_pdf`) y el cajón.
- Funciona igual por los tres caminos, sin driver:
  - **RED**: socket TCP a `IP:9100` (nuevo tipo de conexión).
  - **COM**: Bluetooth SPP o USB-serie (camino COM actual).
  - **WIN RAW**: cualquier cola de Windows con datatype RAW (el driver no renderiza nada), incluida una cola con
    "Generic / Text Only" sobre un puerto USB00x o TCP.
- Se mantiene el camino actual por driver (GDI) como opción por impresora ("Usar driver de Windows"), para las que
  ya funcionan bien así (3nStar, POS-80C instaladas).

### 3.2 Asistente "Agregar impresora" en el agente
- **Red**: escanear la subred local (/24 de cada adaptador activo) buscando el puerto 9100 en paralelo
  (timeout ~300 ms), mostrar IP + MAC + "Imprimir prueba".
- **USB**: listar las impresoras USB conectadas (`USBPRINT`, `Win32_PnPEntity` clase Printer) que no tienen cola;
  crear la cola con "Generic / Text Only" (viene en Windows, no hay que descargar nada) en su puerto USB00x.
- **Bluetooth**: impresoras emparejadas con perfil SPP → su puerto COM (camino COM actual) y botón para abrir
  "Bluetooth y dispositivos" si no está emparejada.
- Opción **"Instalar también en Windows"**: crea el puerto TCP/IP y la cola Generic/Text Only para que otras
  aplicaciones la vean (pide permiso de administrador una vez, UAC).
- Cada impresora agregada guarda: tipo (RED/USB/COM/WIN), destino, ancho (80/58 mm), modo (raster | driver),
  corte y cajón.

### 3.3 POS Matic
- `/impresoras` devuelve también las del agente (red/USB/BT) con un nombre legible ("Térmica red 192.168.1.18");
  `/imprimir` las acepta por ese nombre. Revisar que `useLocalPrintAgent` solo use el nombre (sin cambio en el
  front si es así).

### 3.4 Drivers incluidos
- Con el raster no hace falta ningún driver del fabricante: "Generic / Text Only" viene en Windows.
- Empaquetar drivers de fabricantes (Xprinter XP-80C, POS-80) tiene licencia dudosa y pesa; propuesta: **no
  incluirlos** al principio y dejar un enlace "Instalar driver del fabricante" para quien lo necesite.

## 4. Archivos

- `Models/ImpresoraLocal.cs`: tipos `RED` (Host, Puerto) y `USB`; `Ancho`, `Modo`.
- `Services/DatabaseService.cs`: columnas nuevas con migración suave (ALTER TABLE si no existen).
- `Services/EscPosRaster.cs` (nuevo): bitmap → GS v 0 por franjas, tests con un PDF de ejemplo.
- `Services/PdfPrinterService.cs`: rama raster (Pdfium → bitmap → EscPosRaster → transporte).
- `Services/PrinterService.cs`: transporte TCP (nuevo), COM y RAW (existen).
- `Services/DescubrirImpresoras.cs` (nuevo): escaneo de red, USB sin cola y COM Bluetooth.
- `Dialogs/AgregarImpresoraDialog.xaml` (nuevo, estilo actual del agente) + `ImpresoraDialog` con los tipos nuevos.
- `Services/LocalHttpServer.cs`: `/impresoras` y `/imprimir` con las impresoras del agente.
- README, versión **1.0.7**.

## 5. Pruebas
- Real: térmica de red `192.168.1.18` (ticket con TED desde POS Matic DEMO, comanda, corte y cajón).
- USB y Bluetooth: con una térmica USB y una BT de las que tenga Jean (o un cliente).
- Raster: comparar el ticket impreso con el PDF (TED legible por la app del SII).
- Jean prueba el instalador local **antes** de publicar el release (Velopack).

## 6. Preguntas para Jean
1. ¿Incluimos drivers de fabricantes o basta con imprimir sin driver (raster) + Generic/Text Only de Windows?
2. ¿"Instalar también en Windows" activado por defecto (pide UAC) o solo cuando el usuario lo marca?
3. ¿Raster por defecto para impresoras nuevas y driver de Windows solo para las que ya existen?
