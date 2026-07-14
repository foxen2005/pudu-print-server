# Pudu Print Server — Diseño v2

**Fecha:** 2026-07-14
**Stack:** C# WPF .NET 8 · Velopack · ESCPOS.Net · Supabase · SQLite

---

## 1. Propósito

App de escritorio Windows que corre en segundo plano y actúa como servidor de impresión
para el ecosistema PUDU. Lee jobs desde `cola_impresion` en Supabase (tabla ya existente),
los empareja con impresoras térmicas ESC/POS locales por `zona_id`, formatea los items
del pedido y los imprime automáticamente.

Fuente de jobs: **POS Matic (pos-matic_no)**.
Futura fuente: Totem (misma tabla, sin cambios en este servidor).

---

## 2. Tablas Supabase existentes (no se crean, solo se leen/actualizan)

### `cola_impresion`

| columna         | tipo    | notas                                             |
|-----------------|---------|---------------------------------------------------|
| id              | uuid PK |                                                   |
| user_id         | uuid    | tenant — filtro principal del poll                |
| zona_id         | uuid FK | → zonas_produccion.id — clave de matching         |
| items           | jsonb   | array de items del pedido (raw, sin formatear)    |
| estado          | text    | 'pendiente' → 'procesando' → 'impreso' \| 'error' |
| mesa_numero     | text    | para el header del ticket                         |
| garzon_nombre   | text    | para el header del ticket                         |
| pedido_id       | uuid    | referencia al pedido origen                       |
| intentos        | integer | reintentos, ya existe en la tabla                 |
| error_mensaje   | text    | nullable                                          |
| impresora_ip    | text    | nullable — IP directa (no usada por este server)  |
| impresora_puerto| integer | nullable                                          |
| created_at      | timestamptz |                                               |
| updated_at      | timestamptz |                                               |

### `zonas_produccion`

| columna  | tipo    | notas                          |
|----------|---------|--------------------------------|
| id       | uuid PK |                                |
| nombre   | text    | "cocina", "barra" — display    |
| activo   | boolean |                                |
| user_id  | uuid    |                                |

---

## 3. Arquitectura

```
POS Matic (web)
    │  INSERT cola_impresion { zona_id, items JSON, estado:'pendiente', mesa_numero, garzon_nombre }
    ▼
Supabase — tabla cola_impresion (ya existe)
    │  GET ?estado=pendiente&user_id=eq.{id}  cada ~1.5 s
    ▼
pudu-print-server (Windows, bandeja del sistema)
    │  PATCH cola_impresion → estado:'procesando'
    │  busca impresora local por zona_id
    │  PrintFormatter: items JSON → texto ESC/POS (header + items + corte)
    │  envía bytes a impresora (USB serie / Bluetooth COM / IP:9100)
    │  PATCH cola_impresion → estado:'impreso' | 'error' + error_mensaje
    ▼
Impresora térmica ESC/POS
```

Polling activo cada 1 500 ms (configurable). No Supabase Realtime — polling es suficiente
y más simple en .NET.

---

## 4. Configuración local — SQLite

### tabla `impresoras`

Solo se registran impresoras USB y Bluetooth. Las IP se auto-configuran desde
`zonas_produccion.impresora_ip` + `impresora_puerto` (ya editables en POS Matic web).

| columna         | tipo       | notas                                        |
|-----------------|------------|----------------------------------------------|
| id              | integer PK |                                              |
| zona_id         | text       | UUID de zonas_produccion — clave de matching |
| zona_nombre     | text       | cache del nombre para UI                     |
| tipo_conexion   | text       | 'USB' \| 'BT'                                |
| puerto_com      | text       | "COM3", "COM7", etc.                         |
| activa          | integer    | 0/1                                          |

**Resolución de impresora por job:**
1. Si `zonas_produccion.impresora_ip` tiene valor → imprimir por IP (TcpClient, puerto 9100)
2. Si no → buscar en SQLite local por `zona_id` → usar puerto COM (SerialPort)

### tabla `config`

| columna             | tipo |
|---------------------|------|
| user_id             | text |
| access_token        | text |
| refresh_token       | text |
| negocio_nombre      | text |
| polling_interval_ms | integer |

---

## 5. Servicios internos

| Servicio              | Responsabilidad                                                      |
|-----------------------|----------------------------------------------------------------------|
| `AuthService`         | Login Supabase email/password, refresh token, sesión persistente     |
| `SupabaseService`     | Poll `cola_impresion`, PATCH estado, GET `zonas_produccion`          |
| `PrinterService`      | Envía bytes ESC/POS: SerialPort (USB/BT) o TcpClient (IP:9100)      |
| `PrintFormatter`      | items JSON → texto estructurado → bytes ESC/POS                      |
| `PrintQueueProcessor` | Orquesta: job → busca impresora por zona_id → formatea → imprime     |
| `DatabaseService`     | SQLite local: config + impresoras (mismo patrón SOS pos)             |
| `UpdateService`       | Velopack check/download/apply (copia exacta de SOS pos)              |

### PrintFormatter — formato del ticket

```
================================
  [NEGOCIO NOMBRE]
================================
  Mesa: 5     Garzón: Carlos
  14:23  10/07/2026
--------------------------------
  Qty  Producto
  2x   Cazuela de vacuno
  1x   Jugo naranja
================================
  [corte ESC/POS]
```

---

## 6. Login — primer arranque

Igual que SOS pos:
1. Ventana de login (email + password)
2. POST a Supabase Auth → session
3. Guarda access_token + refresh_token en SQLite
4. Carga `zonas_produccion` activas del usuario → disponibles en config de impresoras

En arranques siguientes: refresh automático del token, sin login si la sesión es válida.

---

## 7. UI — Ventana principal

Abre desde bandeja del sistema (clic izquierdo en ícono de la bandeja).

### Panel de impresoras (grid de tarjetas)

Cada impresora configurada = una tarjeta:
- **Nombre de zona** (ej: "cocina") + ícono tipo conexión (USB / BT / IP)
- **Estado animado** con WPF Storyboard:
  - Idle → impresora estática, color neutro
  - Recibiendo → ring pulsante (ScaleTransform + Opacity loop)
  - Imprimiendo → líneas descendentes animadas (papel saliendo)
  - Error → rojo, ícono exclamación, persiste hasta próximo job exitoso
- Contador de jobs impresos en la sesión actual

### Log de jobs recientes (últimos 20)

```
14:23  cocina    Mesa 5   ✓  impreso
14:21  barra     Mesa 3   ✗  COM3 no responde
```

### Panel de configuración (slide-in lateral)

- Lista de impresoras con editar/eliminar
- Formulario agregar/editar:
  - **Zona** → combobox poblado desde `zonas_produccion` de la cuenta
  - **Tipo** → USB / Bluetooth / IP
  - **Conexión** → COM3 | 192.168.1.100:9100
  - Botón **"Probar conexión"** → imprime ticket de prueba

---

## 8. Empaquetado y distribución

Mismo pipeline que SOS pos:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
vpk pack -u PuduPrintServer -v X.Y.Z -p publish -o velopack-releases
vpk upload github --repoUrl https://github.com/hildenX/pudu-print-server --publish --token $token
```

Dev: `dotnet run` — UpdateService inactivo si no instalado via Velopack.

---

## 9. Gates de calidad

- `/impeccable` en UI (tarjetas, panel config, log) antes de cada release
- `/code-review` sobre el diff completo antes de publicar cada versión
- Firma de commits: `Co-Authored-By: Felix <noreply@anthropic.com>`

---

## 10. Fuera de alcance (v1)

- Impresoras laser/inkjet — excluidas
- Reintentos automáticos con backoff (v2) — `intentos` ya existe en la tabla
- Múltiples instancias del servidor por local
- Dashboard web de monitoreo
- Actualizar `zonas_produccion.impresora_ip` desde este servidor