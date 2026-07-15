namespace PuduPrintServer.Models;

public class ImpresoraLocal
{
    public int    Id              { get; set; }
    public string ZonaId          { get; set; } = "";
    public string ZonaNombre      { get; set; } = "";
    public string TipoConexion    { get; set; } = "WIN"; // WIN | COM
    public string PuertoCom       { get; set; } = "";    // COM3, COM7... (tipo COM)
    public string NombreImpresora { get; set; } = "";    // nombre en Windows (tipo WIN)
    public bool   Activa          { get; set; } = true;

    /// <summary>Destino a mostrar en la lista (impresora de Windows o puerto COM).</summary>
    public string Destino => TipoConexion == "WIN" ? NombreImpresora : PuertoCom;
}
