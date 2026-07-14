namespace PuduPrintServer.Services;

public class UserSession
{
    private static UserSession? _i;
    public static UserSession Instance => _i ??= new UserSession();

    public string? UserId        { get; set; }
    public string? AccessToken   { get; set; }
    public string? RefreshToken  { get; set; }
    public string? NombreNegocio { get; set; }

    public void Clear()
    {
        UserId = null; AccessToken = null;
        RefreshToken = null; NombreNegocio = null;
    }
}
