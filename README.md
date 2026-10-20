# 🖨️ Pudu Print Server

Aplicación de escritorio para Windows que actúa como **agente de impresión** para el ecosistema PUDU (sistema de gestión de restaurantes). Tiene dos caminos independientes:

1. **Comandas a cocina** — escucha la cola de impresión en Supabase y reenvía los trabajos en ESC/POS a impresoras térmicas locales (USB/Bluetooth vía COM o spooler de Windows) o de red (TCP/IP).
2. **Ticket de caja (PDF con timbre TED)** — servidor HTTP en loopback (`127.0.0.1:21777`) al que el POS, corriendo en la misma PC, le envía el PDF ya generado; el agente lo imprime silenciosamente por el driver de Windows.

---

## ¿Cómo funciona?

### Camino 1 — Comandas (cola en Supabase)

```
POS Matic (tablet/PC en el local)
    │
    │  Encola trabajo en Supabase
    ▼
cola_impresion (Supabase DB)  ◄──── Pudu Print Server (este programa)
                                         │  polling cada 1.5 s
                                         │
                    ┌────────────────────┼────────────────────┐
                    ▼                    ▼                    ▼
          Impresora de red (IP)   Impresora USB/BT (COM)   Spooler Windows
          TCP port 9100           SerialPort               RAW (RawPrinterHelper)
```

1. **Login**: El usuario inicia sesión con su cuenta PUDU (Supabase Auth). El token se guarda localmente en SQLite para auto-login al reiniciar.
2. **Polling**: Cada 1.5 segundos consulta `cola_impresion` buscando jobs con `estado = pendiente` asociados a su `user_id`.
3. **Procesamiento**: Por cada job:
   - Cambia estado → `procesando`
   - Formatea la comanda en ESC/POS
   - Envía bytes a la impresora (IP, COM o spooler)
   - Cambia estado → `impreso` o `error`
4. **Actualización automática**: Velopack verifica actualizaciones al iniciar.

### Camino 2 — Ticket de caja (HTTP local, misma PC)

```
POS Matic (Chrome, https://pudusystem.com)  ──►  http://127.0.0.1:21777  (este programa)
   genera el PDF con TED                            │ PdfiumViewer → GDI → driver Windows
   POST /imprimir { pdfBase64, impresora }          ▼
                                             Impresora térmica USB (ej. 3nStar, POS-80C)
```

- **No requiere login** ni Supabase: el PDF viaja en el request. Funciona apenas arranca el agente.
- Binding solo a `127.0.0.1` → sin permisos de admin, sin `urlacl`, sin firewall.
- **CORS restringido** a `*.pudusystem.com`, `*.pudutecnologia.cl` y `localhost`. Responde `Access-Control-Allow-Private-Network: true` para que Chrome permita la llamada HTTPS → loopback (Private Network Access).
- Puerto configurable en SQLite (`config.http_port`); impresora por defecto en `config.impresora_ticket` (el POS normalmente la envía en el request).

| Método | Ruta | Body | Respuesta |
|---|---|---|---|
| `GET` | `/health` | — | `{ ok, servicio, puerto, version }` — detección del agente (`version` desde 1.0.6, ej. `"1.0.6"`) |
| `GET` | `/impresoras` | — | `{ ok, impresoras: string[] }` — impresoras de Windows |
| `POST` | `/imprimir` | `{ pdfBase64, impresora?, abrirCajon? }` | `{ ok }` o `{ ok:false, error }`. Desde 1.0.6 imprime solo las páginas reales (omite la última página si tiene menos de 1 mm de contenido: la página fantasma de html2pdf; lo deja en el log) y después manda un corte RAW (`cortar_despues_pdf`) |
| `POST` | `/cajon` | `{ impresora? }` | abre el cajón monedero (pulso ESC/POS por RAW) |
| `POST` | `/comanda` | ver tabla de abajo | comanda a cocina **directa** (misma resolución de impresora que la cola). Idempotente por `id`: un reintento no reimprime |

Desde v1.0.4 POS Matic manda las comandas por `/comanda` cuando el agente responde (switch "Comandas de cocina por el agente" en Configuración) y solo cae a `cola_impresion` si no. Así las comandas salen por la LAN **sin internet**.

#### Campos de `POST /comanda` (y columnas equivalentes de `cola_impresion`)

| Campo `/comanda` | Columna `cola_impresion` | Tipo | Desde | Uso en el papel |
|---|---|---|---|---|
| `id` | `id` | uuid | 1.0.4 | idempotencia (el mismo id por `/comanda` y por la cola sale una sola vez) |
| `zonaId` | `zona_id` | uuid | 1.0.4 | resolución de impresora local (USB/COM) |
| `zonaNombre` | join `zonas_produccion(nombre)` (limpio) | text | 1.0.4 | el POS lo sigue mandando con el rótulo viejo pegado ("Barra · ADICIONAL #2") para el agente 1.0.5. En 1.0.6, sin `tipoComanda` se deduce el rótulo de ese sufijo |
| `zonaBase` | — | text | 1.0.6 | nombre limpio de la zona ("Cocina") → "─ C O C I N A ─". Con `tipoComanda` y sin `zonaBase` se usa lo de `zonaNombre` antes de " · " |
| `mesaNumero` | `mesa_numero` | text | 1.0.4 | "MESA  S.8" grande |
| `garzonNombre` | `garzon_nombre` | text | 1.0.4 | fila "Garzón" |
| `pedidoId` | `pedido_id` | uuid | 1.0.4 | — |
| `comensales` | `comensales` | int | 1.0.5 | fila "Comensales" (solo si > 0) |
| `notas` | `notas` | text | 1.0.5 | recuadro "* NOTA" en negrita, antes de los productos |
| `items` | `items` | CartItem[] (o string JSON) | 1.0.4 | por ítem: `nombre`, `quantity` (**decimal** desde 1.0.6: `1.5`, `0.25`, `"1,5"`; negativa en anulación), `descripcion` ("-> sin cebolla"), `ya_enviado` |
| `items[].ya_enviado` | dentro de `items` (texto JSON) | bool (también acepta número > 0 o `yaEnviado`) | 1.0.6 | √ al final de la línea = ya en producción |
| `tipoComanda` (o `tipo_comanda`) | `tipo_comanda` | `comanda` \| `adicional` \| `reimpresion` \| `anulacion` \| `marcado` | 1.0.6 | `adicional` → "+ ADICIONAL #n"; `reimpresion` → franja "REIMPRESIÓN" + "No preparar de nuevo" (todo con √) o "√ = ya en producción" (mezcla); `anulacion` → franja liviana entre dos líneas, "A N U L A R" (doble ancho, negrita) + "No preparar lo siguiente", con cantidades negativas |
| `secuencia` | `secuencia` | int (por zona) | 1.0.6 | el `n` de "ADICIONAL #n"; solo se imprime si `tipoComanda` = `adicional` |
| `impresoraIp`, `impresoraPuerto` | `impresora_ip`, `impresora_puerto` | text, int | 1.0.4 | impresora de red (TCP 9100) |

Si `tipoComanda`/`secuencia`/`ya_enviado` no vienen (POS anterior), la comanda sale igual con el diseño nuevo; una reimpresión sin `ya_enviado` se trata como "todo ya enviado" (√ en todo, "No preparar de nuevo"). Un ítem ilegible sale como "?× (ERROR LEYENDO ÍTEM)" sin tirar el resto de la comanda.

#### Formato de la comanda (1.0.6, texto ESC/POS)

Imita el ticket aprobado en `pos-matic_no/maqueta-mesa.html` (bloque "Comanda v2"). Ejemplo a 48 columnas:

```
                 C O M A N D A                    (doble alto + doble ancho)
                 ─ B A R R A ─
────────────────────────────────────────────────
             R E I M P R E S I Ó N
              √ = ya en producción
────────────────────────────────────────────────
M E S A                                   S . 8   (doble alto + doble ancho)
Garzón                                      raul
Comensales                                     5
Fecha/hora                      20-10-2026 22:04
┌──────────────────────────────────────────────┐
│ * NOTA                                       │
│ Alergia al maní                              │
└──────────────────────────────────────────────┘
 2× PISCO SOUR                                 √  (cantidad en doble alto)
················································
 1× AGUA MINERAL 500ML
    -> sin hielo
                 TOTAL ÍTEMS: 3
------------------------------------------------
      Documento interno · solo producción
```

- Tabla de caracteres: el texto se codifica en CP850 y desde 1.0.6 se envía `ESC t 2` (PC850) tras cada `ESC @`. El √ no existe en CP850: se emite `ESC t 0` (PC437) + `0xFB` + `ESC t 2`.
- Corte: `GS V 65 0` (corte **completo**, "función B": la impresora avanza sola hasta la cuchilla y corta, sin salto de línea previo) en vez de `Feed(4)` + `GS V 65 3` → ~2 cm menos de papel por comanda.
- Todo lo que el agente manda por ESC/POS (comandas y tickets de prueba) arranca con `ESC @` + `ESC t 2` (`EscPos.Initialize`).
- Los textos variables (zona, mesa, garzón, nota, nombre y descripción del ítem) se normalizan a lo que imprime CP850 antes de calcular columnas: un emoji sale como un `?` de una columna y el recuadro no se descuadra.
- Nombres largos saltan de línea con sangría (no se truncan).

---

## Sin conexión a Supabase

El agente **no se cierra** sin internet. El header pasa a **"● No hay impresoras en línea"** (rojo) y el poll de la cola reintenta cada 5 s (1.5 s cuando está online); al recuperar la conexión vuelve a "● Activo". Mientras tanto el servidor HTTP local (`/imprimir`, `/cajon`, `/comanda`) sigue funcionando. Al arrancar sin red con sesión guardada, usa el token guardado y renueva la sesión solo cuando vuelve la conexión (si Supabase responde 401, reintenta el refresh como máximo una vez por minuto; nunca cierra sesión sola).

---

## Logs

Un archivo de texto por día, botón **"📄 Logs"** en la ventana principal abre la carpeta:

```
%LocalAppData%\PuduPrintServer\logs\print-server-YYYY-MM-DD.txt
```

(fuera de `current\` para que sobreviva a las actualizaciones de Velopack; en dev queda en `bin\...\logs\`). Formato `fecha hora  [NIVEL]  [origen]  mensaje` con origen `app` / `auth` / `cola` / `local`. Se conservan 14 días. "Actividad reciente" en la ventana muestra las mismas líneas.

> **Nota histórica:** se probó SumatraPDF para el PDF silencioso y se descartó — su `-print-to -silent` no genera trabajo de spool en varios drivers térmicos (verificado en POS-80C). Ver `tools/README.md`.

---

## Requisitos para desarrollar

### Herramientas obligatorias

| Herramienta | Versión | Link |
|---|---|---|
| **.NET SDK** | 8.0 o superior | https://dotnet.microsoft.com/download |
| **Visual Studio** | 2022 (recomendado) | Con workload **"Escritorio .NET"** |
| **Git** | Cualquier versión | https://git-scm.com |

### Herramientas opcionales (para publicar releases)

| Herramienta | Uso |
|---|---|
| **Velopack CLI** (`vpk`) | Crear instaladores y actualizaciones |
| **GitHub CLI** (`gh`) | Push de releases al repositorio |

Instalar Velopack CLI:
```bash
dotnet tool install -g vpk
```

---

## Clonar y compilar

```bash
# Clonar
git clone https://github.com/foxen2005/pudu-print-server.git
cd pudu-print-server

# Restaurar dependencias y compilar
dotnet build src/PuduPrintServer/PuduPrintServer.csproj

# Ejecutar en modo desarrollo
dotnet run --project src/PuduPrintServer/PuduPrintServer.csproj
```

O desde **Visual Studio 2022**: abrir `src/PuduPrintServer/PuduPrintServer.csproj` y presionar `F5`.

---

## Estructura del proyecto

```
pudu-print-server/
├── src/
│   └── PuduPrintServer/
│       ├── Services/
│       │   ├── AuthService.cs          # Login y refresh de sesión con Supabase Auth
│       │   ├── SupabaseService.cs      # Consultas REST a cola_impresion y zonas_produccion
│       │   ├── PrintQueueProcessor.cs  # Loop de polling y despacho de trabajos
│       │   ├── PrinterService.cs       # Envío a impresora IP (TCP), COM (USB/BT) o spooler
│       │   ├── RawPrinterHelper.cs     # Envío RAW al spooler de Windows (winspool)
│       │   ├── PrintFormatter.cs       # Formateo de comanda en ESC/POS
│       │   ├── EscPos.cs               # Comandos ESC/POS primitivos
│       │   ├── LocalHttpServer.cs      # HTTP loopback 127.0.0.1:21777 (/health /impresoras /imprimir /cajon /comanda)
│       │   ├── PdfPrinterService.cs    # Imprime PDF con PdfiumViewer (GDI) al driver de Windows
│       │   ├── LogService.cs           # Log a .txt diario (logs\) + alimenta "Actividad reciente"
│       │   ├── DatabaseService.cs      # SQLite local (config + impresoras registradas)
│       │   ├── UserSession.cs          # Sesión en memoria (singleton)
│       │   └── UpdateService.cs        # Auto-actualización vía Velopack
│       ├── Views/
│       │   ├── LoginWindow.xaml        # Pantalla de login
│       │   └── MainWindow.xaml         # Ventana principal + tray icon
│       ├── Models/                     # DTOs y modelos de datos
│       ├── Assets/                     # Iconos
│       └── PuduPrintServer.csproj
├── docs/
│   ├── plans/                          # Planes de desarrollo
│   └── specs/                          # Especificaciones técnicas
├── tools/                              # (vacío) nota sobre SumatraPDF descartado
├── publish/                            # Artefactos de publicación (ignorados por git)
└── velopack-releases/                  # Releases de Velopack
```

---

## Dependencias NuGet

| Paquete | Versión | Uso |
|---|---|---|
| `Hardcodet.NotifyIcon.Wpf` | 2.0.1 | Ícono en la bandeja del sistema (system tray) |
| `Microsoft.Data.Sqlite` | 9.0.7 | Base de datos local para config e impresoras |
| `PdfiumViewer` | 2.13.0 | Render e impresión GDI del PDF del ticket de caja |
| `PdfiumViewer.Native.x86_64.v8-xfa` | 2018.4.8.256 | `pdfium.dll` nativo (llega por NuGet, sin binarios externos) |
| `System.Drawing.Common` | 8.0.0 | Requerido por PdfiumViewer |
| `System.IO.Ports` | 9.0.7 | Comunicación con impresoras USB/BT vía COM |
| `Velopack` | 1.2.0 | Auto-actualización de la aplicación |

---

## Base de datos local (SQLite)

El archivo se crea automáticamente en:
```
C:\Users\<usuario>\pudu-print-server\print-server.db
```

Tablas:

```sql
-- Configuración general (tokens de sesión, nombre del negocio,
-- http_port e impresora_ticket para el servidor HTTP local)
CREATE TABLE config (
    clave TEXT PRIMARY KEY,
    valor TEXT NOT NULL
);
```

Claves de `config` que se pueden ajustar a mano (no hay pantalla para estas):

| Clave | Default | Desde | Uso |
|---|---|---|---|
| `http_port` | `21777` | 1.0.3 | puerto del servidor HTTP local |
| `impresora_ticket` | — | 1.0.3 | impresora de Windows para `/imprimir` y `/cajon` si el POS no la manda |
| `ancho_columnas` | `48` | 1.0.6 | columnas de la comanda: `48` = 80 mm (fuente A), `32` = 58 mm. Rango aceptado 24–64 |
| `cortar_despues_pdf` | `true` | 1.0.6 | después de imprimir un PDF por `/imprimir`, manda `GS V 65 0` (corte completo) por RAW. **Si el driver de Windows de esa impresora ya corta al final del documento, ponerlo en `false`** para evitar el doble corte. El valor efectivo queda en el log al arrancar (`cortar_despues_pdf = true/false`) |

Ejemplo (con la app cerrada, `sqlite3` sobre `print-server.db`):

```sql
INSERT INTO config(clave, valor) VALUES('ancho_columnas', '32')
  ON CONFLICT(clave) DO UPDATE SET valor = excluded.valor;
```

```sql

-- Impresoras locales registradas (USB/BT por puerto COM)
CREATE TABLE impresoras (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    zona_id       TEXT NOT NULL,
    zona_nombre   TEXT NOT NULL,
    tipo_conexion TEXT NOT NULL DEFAULT 'USB',
    puerto_com    TEXT NOT NULL,
    activa        INTEGER NOT NULL DEFAULT 1
);
```

---

## Supabase

El proyecto apunta al proyecto Supabase de PUDU. Las claves (`AnonKey`, `BaseUrl`) están hardcodeadas en `AuthService.cs` y `SupabaseService.cs`.

Tablas de Supabase que usa:

| Tabla | Operaciones |
|---|---|
| `cola_impresion` | SELECT (jobs pendientes), PATCH (cambiar estado) |
| `zonas_produccion` | SELECT (datos de impresoras de red) |

---

## Publicar una nueva versión

```bash
# 1. Compilar en Release
dotnet publish src/PuduPrintServer/PuduPrintServer.csproj -c Release -r win-x64

# 2. Empaquetar con Velopack
vpk pack -u PuduPrintServer -v 1.0.0 -p ./publish/win-x64 -o ./velopack-releases

# 3. Push de la release a GitHub
gh release create v1.0.0 ./velopack-releases/* --title "v1.0.0"
```

---

## Notas importantes

- **Solo Windows**: La app usa WPF + `System.IO.Ports`, no es multiplataforma.
- **Self-contained**: El ejecutable incluye el runtime de .NET, no requiere instalación en el PC destino.
- **Sin variables de entorno**: La config de Supabase está hardcodeada. Si se cambia el proyecto Supabase, actualizar `AuthService.cs` y `SupabaseService.cs`.
- **Impresoras IP**: La IP y puerto de la impresora vienen directamente en el job de `cola_impresion` (copiados por POS Matic desde `zonas_produccion`).
- **Impresoras USB/BT**: Se configuran manualmente en la app y se guardan en SQLite local.
- **Ticket de caja**: No pasa por Supabase. El POS lo manda por HTTP a `127.0.0.1:21777`; si el agente no corre, el POS cae al diálogo de impresión del navegador. Cliente en pos-matic: `src/hooks/useLocalPrintAgent.ts`.
