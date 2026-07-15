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
                tipo_conexion TEXT NOT NULL DEFAULT 'USB',
                puerto_com   TEXT NOT NULL,
                activa       INTEGER NOT NULL DEFAULT 1
            );
            """;
        cmd.ExecuteNonQuery();
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
        cmd.CommandText = "SELECT id,zona_id,zona_nombre,tipo_conexion,puerto_com,activa FROM impresoras ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<ImpresoraLocal>();
        while (r.Read())
            list.Add(new ImpresoraLocal {
                Id           = r.GetInt32(0),
                ZonaId       = r.GetString(1),
                ZonaNombre   = r.GetString(2),
                TipoConexion = r.GetString(3),
                PuertoCom    = r.GetString(4),
                Activa       = r.GetInt32(5) == 1
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
                INSERT INTO impresoras(zona_id,zona_nombre,tipo_conexion,puerto_com,activa)
                VALUES(@zi,@zn,@tc,@pc,@ac)
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE impresoras SET zona_id=@zi,zona_nombre=@zn,tipo_conexion=@tc,
                puerto_com=@pc,activa=@ac WHERE id=@id
                """;
            cmd.Parameters.AddWithValue("@id", imp.Id);
        }
        cmd.Parameters.AddWithValue("@zi", imp.ZonaId);
        cmd.Parameters.AddWithValue("@zn", imp.ZonaNombre);
        cmd.Parameters.AddWithValue("@tc", imp.TipoConexion);
        cmd.Parameters.AddWithValue("@pc", imp.PuertoCom);
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
