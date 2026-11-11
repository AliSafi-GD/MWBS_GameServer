namespace GameServer.Core;

/// <summary>Bound from the "GameServer" config section (appsettings.json or env vars like GameServer__ModulesPath).</summary>
public sealed class ServerOptions
{
    /// <summary>Folder containing one subfolder per module: modules/&lt;Name&gt;/&lt;Name&gt;.dll</summary>
    public string ModulesPath { get; set; } = "modules";
}
