using GameServer.Core.Social;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>Validation for config writes, plus the two keys the server itself understands.</summary>
public static partial class ConfigRules
{
    /// <summary>{ "enabled": bool, "message": any JSON, "until": any JSON }. While enabled, /v1/* answers 503 "maintenance".</summary>
    public const string Maintenance = "maintenance";

    /// <summary>{ "&lt;store&gt;": { "latest", "minimum", "url" }, "default": {...} }. Used by GET /v1/version-check.</summary>
    public const string AppVersion = "app_version";

    /// <summary>{ "currencies": { "coins": { "name": ..., "max": 999 } }, "items": { "card_fire": { "stack": 99 } } }. Enforced by the wallet.</summary>
    public const string Economy = "economy";

    /// <summary>{ "&lt;id&gt;": { "sort": "desc|asc", "operator": "best|set|incr", "reset": "never|daily|weekly|monthly" } }</summary>
    public const string Leaderboards = "leaderboards";

    /// <summary>{ "&lt;id&gt;": { "start", "end", "sort", "operator", "entryFee": {...}, "rewards": [...] } }</summary>
    public const string Tournaments = "tournaments";

    /// <summary>{ "maxFriends", "maxPendingInvites", "groupTypes": { "&lt;type&gt;": { "maxPerUser", "maxMembers" } } }</summary>
    public const string Social = "social";

    /// <summary>{ "modes": { "&lt;mode&gt;": { "handler", "transport", "minPlayers", "maxPlayers", "tickRate", "rules": {...} } } }</summary>
    public const string Matches = "matches";

    /// <summary>{ "products": { "&lt;id&gt;": { "name", "cost": {...}, "gives": {...}, "limit": { "per", "count" }, "startUtc", "endUtc" } } }</summary>
    public const string Shop = "shop";

    /// <summary>{ "achievements": { "&lt;id&gt;": { "name", "stat", "target", "reward": {...} } } }</summary>
    public const string Achievements = "achievements";

    /// <summary>{ "quests": { "&lt;id&gt;": { "name", "stat", "target", "period": "day|week|month", "reward": {...} } } }</summary>
    public const string Quests = "quests";

    /// <summary>{ "utcOffsetMinutes", "resetOnMiss", "rewards": [ { "wallet": {...}, "items": {...} }, ... ] }</summary>
    public const string DailyRewards = "daily_rewards";

    private const int MaxValueChars = 64 * 1024;

    /// <summary>Clients must be able to read the maintenance message, so that key is always public.</summary>
    public static bool IsAlwaysPublic(string key) => key == Maintenance;

    /// <summary>Names of currencies, items, leaderboards and tournaments: 1-64 of a-z 0-9 _ - . (not starting with a symbol).</summary>
    public static bool IsValidName(string name) => KeyRegex().IsMatch(name);

    public static void ValidateKey(string key)
    {
        if (!KeyRegex().IsMatch(key))
            throw new GameException("invalid_config_key",
                "Key must be 1-64 characters: lowercase letters, digits, '_', '-' or '.', starting with a letter or digit.");
    }

    public static void ValidateValue(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new GameException("invalid_config", "\"value\" is required.");
        if (value.GetRawText().Length > MaxValueChars)
            throw new GameException("config_too_large", "A config value can be at most 64 KB.");

        if (key == Maintenance)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GameException("invalid_config", "maintenance must be an object with a boolean \"enabled\".");
        }
        else if (key == AppVersion && value.ValueKind != JsonValueKind.Object)
        {
            throw new GameException("invalid_config", "app_version must be an object keyed by store name.");
        }
        else if (key == Economy)
        {
            EconomyCatalog.Parse(value);
        }
        else if (key == Leaderboards)
        {
            GameDefinitions.ParseLeaderboards(value);
        }
        else if (key == Tournaments)
        {
            GameDefinitions.ParseTournaments(value);
        }
        else if (key == Social)
        {
            SocialRules.Parse(value);
        }
        else if (key == Achievements)
        {
            GameServer.Core.Progression.GoalCatalog.Parse(GameServer.Core.Progression.GoalCatalog.Achievement, value);
        }
        else if (key == Quests)
        {
            GameServer.Core.Progression.GoalCatalog.Parse(GameServer.Core.Progression.GoalCatalog.Quest, value);
        }
        else if (key == DailyRewards)
        {
            GameServer.Core.Progression.DailyCatalog.Parse(value);
        }
        else if (key == Shop)
        {
            GameServer.Core.Shop.ShopCatalog.Parse(value);
        }
        else if (key == Matches)
        {
            GameServer.Core.Matches.MatchModes.Parse(value);
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.\-]{0,63}\z")]
    private static partial Regex KeyRegex();
}
