using System.Text.Json;

namespace GameServer.Core.GameConfig;

/// <summary>One config key as it ships in a file: <c>&lt;key&gt;.json</c> holding <c>{ "public": false, "value": { ... } }</c>.</summary>
/// <param name="Path">Where it was read from (for messages).</param>
public sealed record GameConfigFile(string Key, JsonElement Value, bool IsPublic, string Path)
{
    /// <summary>The public flag the database will get: the file's flag, or true for keys that must always be public.</summary>
    public bool EffectivePublic => IsPublic || ConfigRules.IsAlwaysPublic(Key);
}
