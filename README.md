# 🖨️ Pudu Print Server

Aplicación de escritorio para Windows que actúa como **servidor de impresión** para el ecosistema PUDU (sistema de gestión de restaurantes). Escucha la cola de impresión en Supabase y reenvía los trabajos a impresoras térmicas locales (USB/Bluetooth vía puerto COM) o de red (TCP/IP).

---

## ¿Cómo funciona?

```
POS Matic (tablet/PC en el local)
    │
    │  Encola trabajo en Supabase
    ▼
cola_impresion (Supabase DB)  ◄──── Pudu Print Server (este programa)
                                         │  polling cada 1.5 s
                                         │
                              ┌──────────┴──────────┐
                              ▼                      ▼
                    Impresora de red (IP)    Impresora USB/BT (COM)
                    TCP port 9100            SerialPort
```

1. **Login**: El usuario inicia sesión con su cuenta PUDU (Supabase Auth). El token se guarda localmente en SQLite para auto-login al reiniciar.
2. **Polling**: Cada 1.5 segundos consulta `cola_impresion` buscando jobs con `estado = pendiente` asociados a su `user_id`.
3. **Procesamiento**: Por cada job:
   - Cambia estado → `procesando`
   - Formatea la comanda en ESC/POS
   - Envía bytes a la impresora (IP o COM)
   - Cambia estado → `impreso` o `error`
4. **Actualización automática**: Velopack verifica actualizaciones al iniciar.

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
│       │   ├── PrinterService.cs       # Envío a impresora IP (TCP) o COM (USB/BT)
│       │   ├── PrintFormatter.cs       # Formateo de comanda en ESC/POS
│       │   ├── EscPos.cs               # Comandos ESC/POS primitivos
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
├── publish/                            # Artefactos de publicación (ignorados por git)
└── velopack-releases/                  # Releases de Velopack
```

---

## Dependencias NuGet

| Paquete | Versión | Uso |
|---|---|---|
| `Hardcodet.NotifyIcon.Wpf` | 2.0.1 | Ícono en la bandeja del sistema (system tray) |
| `Microsoft.Data.Sqlite` | 9.0.7 | Base de datos local para config e impresoras |
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
-- Configuración general (tokens de sesión, nombre del negocio)
CREATE TABLE config (
    clave TEXT PRIMARY KEY,
    valor TEXT NOT NULL
);

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
