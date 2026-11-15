namespace GameServer.Core.GameConfig;

/// <summary>Bound from "GameServer:GameConfig" (env GameServer__GameConfig__Path / __Mode).</summary>
public sealed class GameConfigOptions
{
    /// <summary>
    /// Optional extra folder with &lt;key&gt;.json files, for config that ops manages outside the game module.
    /// Relative paths are resolved against the host's content root. A key may not exist here and in a module.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>"seed" (default, also when empty), "sync" or "off". See <see cref="GameConfigMode"/>.</summary>
    public string Mode { get; set; } = "";
}

public enum GameConfigMode
{
    /// <summary>Write only the keys that are missing in the database. Edits made in the admin panel stay.</summary>
    Seed,

    /// <summary>The files are the source of truth: a key whose value or public flag differs is overwritten at startup.</summary>
    Sync,

    /// <summary>Ignore the files.</summary>
    Off
}

public static class GameConfigModes
{
    public static GameConfigMode Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return GameConfigMode.Seed;
        return text.Trim().ToLowerInvariant() switch
        {
            "seed" => GameConfigMode.Seed,
            "sync" => GameConfigMode.Sync,
            "off" => GameConfigMode.Off,
            _ => throw new GameConfigException(
                $"GameServer:GameConfig:Mode is '{text}'. Use seed (default), sync or off.")
        };
    }
}
