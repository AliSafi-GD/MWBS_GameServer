using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.GameConfig;

/// <summary>
/// Checks a config file the same way a write through the admin API is checked: the key name, and the value with the
/// parsers the services use (<see cref="ConfigRules.ValidateValue"/> covers shop, quests, matches, economy and so on).
/// No database needed.
/// </summary>
public static class GameConfigValidator
{
    /// <summary>Returns null when the file is fine, otherwise the problem with the file path in front.</summary>
    public static string? Check(GameConfigFile file)
    {
        try
        {
            ConfigRules.ValidateKey(file.Key);
            ConfigRules.ValidateValue(file.Key, file.Value);
            return null;
        }
        catch (GameException ex)
        {
            return $"{file.Path}: {ex.Message}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or KeyNotFoundException)
        {
            return $"{file.Path}: {ex.Message}";
        }
    }
}
