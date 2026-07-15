using System.IO;
using Microsoft.Data.Sqlite;
using PuduPrintServer.Models;

namespace PuduPrintServer.Services;

public class DatabaseService
{
    private static DatabaseService? _i;
    public static DatabaseService Instance => _i ??= new DatabaseService();

    private readonly string _db = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "pudu-print-server", "print-server.db");

    private DatabaseService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS config (
                clave TEXT PRIMARY KEY,
                valor TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS impresoras (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                zona_id      TEXT NOT NULL,
                zona_nombre  TEXT NOT NULL,
                tipo_conexion TEXT NOT NULL DEFAULT 'WIN',
                puerto_com   TEXT NOT NULL DEFAULT '',
                nombre_impresora TEXT NOT NULL DEFAULT '',
                activa       INTEGER NOT NULL DEFAULT 1
            );
            """;
        cmd.ExecuteNonQuery();

        // Migración: agregar nombre_impresora a BDs creadas antes de esta versión
        if (!ColumnaExiste(con, "impresoras", "nombre_impresora"))
        {
            using var alter = con.CreateCommand();
            alter.CommandText = "ALTER TABLE impresoras ADD COLUMN nombre_impresora TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
    }

    private static bool ColumnaExiste(SqliteConnection con, string tabla, string columna)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tabla})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), columna, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_db}");
        con.Open();
        return con;
    }

    public string? GetConfig(string clave)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT valor FROM config WHERE clave=@c";
        cmd.Parameters.AddWithValue("@c", clave);
        return cmd.ExecuteScalar() as string;
    }

    public void SetConfig(string clave, string valor)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO config(clave,valor) VALUES(@c,@v) ON CONFLICT(clave) DO UPDATE SET valor=@v";
        cmd.Parameters.AddWithValue("@c", clave);
        cmd.Parameters.AddWithValue("@v", valor);
        cmd.ExecuteNonQuery();
    }

    public void DeleteConfig(string clave)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM config WHERE clave=@c";
        cmd.Parameters.AddWithValue("@c", clave);
        cmd.ExecuteNonQuery();
    }

    public List<ImpresoraLocal> GetImpresoras()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,zona_id,zona_nombre,tipo_conexion,puerto_com,nombre_impresora,activa FROM impresoras ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<ImpresoraLocal>();
        while (r.Read())
            list.Add(new ImpresoraLocal {
                Id              = r.GetInt32(0),
                ZonaId          = r.GetString(1),
                ZonaNombre      = r.GetString(2),
                TipoConexion    = r.GetString(3),
                PuertoCom       = r.GetString(4),
                NombreImpresora = r.GetString(5),
                Activa          = r.GetInt32(6) == 1
            });
        return list;
    }

    public void GuardarImpresora(ImpresoraLocal imp)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        if (imp.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO impresoras(zona_id,zona_nombre,tipo_conexion,puerto_com,nombre_impresora,activa)
                VALUES(@zi,@zn,@tc,@pc,@ni,@ac)
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE impresoras SET zona_id=@zi,zona_nombre=@zn,tipo_conexion=@tc,
                puerto_com=@pc,nombre_impresora=@ni,activa=@ac WHERE id=@id
                """;
            cmd.Parameters.AddWithValue("@id", imp.Id);
        }
        cmd.Parameters.AddWithValue("@zi", imp.ZonaId);
        cmd.Parameters.AddWithValue("@zn", imp.ZonaNombre);
        cmd.Parameters.AddWithValue("@tc", imp.TipoConexion);
        cmd.Parameters.AddWithValue("@pc", imp.PuertoCom);
        cmd.Parameters.AddWithValue("@ni", imp.NombreImpresora);
        cmd.Parameters.AddWithValue("@ac", imp.Activa ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void EliminarImpresora(int id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM impresoras WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
