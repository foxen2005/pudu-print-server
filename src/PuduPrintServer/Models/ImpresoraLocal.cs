namespace PuduPrintServer.Models;

public class ImpresoraLocal
{
    public int    Id            { get; set; }
    public string ZonaId        { get; set; } = "";
    public string ZonaNombre    { get; set; } = "";
    public string TipoConexion  { get; set; } = "USB"; // USB | BT
    public string PuertoCom     { get; set; } = "";    // COM3, COM7...
    public bool   Activa        { get; set; } = true;
}
