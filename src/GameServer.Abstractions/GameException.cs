namespace GameServer.Abstractions;

/// <summary>Throw from a module to return a clean client error: { "error": code, "message": message }.</summary>
public sealed class GameException : Exception
{
    public GameException(string code, string? message = null) : base(message ?? code)
    {
        Code = code;
    }

    public string Code { get; }
}
