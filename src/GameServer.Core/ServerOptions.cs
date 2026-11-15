using GameServer.Core.GameConfig;

namespace GameServer.Core;

/// <summary>Bound from the "GameServer" config section (appsettings.json or env vars like GameServer__ModulesPath).</summary>
public sealed class ServerOptions
{
    /// <summary>
    /// Folder containing one subfolder per module: modules/&lt;Name&gt;/&lt;Name&gt;.dll. A relative path is taken from the
    /// host's content root (not the working directory); use an absolute path for a service.
    /// </summary>
    public string ModulesPath { get; set; } = "modules";

    /// <summary>Game config files: where they come from and how they are applied (GameServer:GameConfig).</summary>
    public GameConfigOptions GameConfig { get; set; } = new();
}
