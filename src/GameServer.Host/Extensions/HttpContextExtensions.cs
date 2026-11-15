namespace GameServer.Host;

public static class HttpContextExtensions
{
    /// <summary>The id of the signed-in player (the JWT "sub" claim). Only call it on endpoints that require authorization.</summary>
    public static string UserId(this HttpContext http) => http.User.FindFirst("sub")!.Value;
}
