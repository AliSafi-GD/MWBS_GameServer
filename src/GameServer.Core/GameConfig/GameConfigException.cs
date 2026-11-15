namespace GameServer.Core.GameConfig;

/// <summary>A game config file is missing, unreadable or invalid. The message names the file and the reason.</summary>
public sealed class GameConfigException : Exception
{
    public GameConfigException(string message) : base(message) { }
}
