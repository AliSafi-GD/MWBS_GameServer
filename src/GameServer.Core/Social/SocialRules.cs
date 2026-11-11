using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Social;

public sealed record GroupTypeRule(int MaxPerUser, int MaxMembers);

/// <summary>
/// Limits from the "social" config key. Everything is optional:
/// <c>{ "maxFriends": 200, "groupTypes": { "clan": { "maxPerUser": 1, "maxMembers": 50 }, "group": { "maxPerUser": 10, "maxMembers": 100 } } }</c>.
/// If "groupTypes" is given it replaces the default types.
/// </summary>
public sealed class SocialRules
{
    public const int DefaultMaxFriends = 200;

    private static readonly IReadOnlyDictionary<string, GroupTypeRule> DefaultTypes =
        new Dictionary<string, GroupTypeRule>(StringComparer.Ordinal)
        {
            ["clan"] = new GroupTypeRule(1, 50),
            ["group"] = new GroupTypeRule(10, 100)
        };

    public int MaxFriends { get; private init; } = DefaultMaxFriends;

    /// <summary>How many invitations a player may have waiting for an answer.</summary>
    public int MaxPendingInvites => 100;

    public IReadOnlyDictionary<string, GroupTypeRule> GroupTypes { get; private init; } = DefaultTypes;

    public GroupTypeRule RuleFor(string type) => GroupTypes.TryGetValue(type, out var rule) ? rule : new GroupTypeRule(10, 100);

    public static SocialRules From(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } v ? Parse(v) : new SocialRules();

    /// <summary>Throws GameException("invalid_config") when the value is not valid. Also used to validate config writes.</summary>
    public static SocialRules Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", "social must be an object.");

        var maxFriends = DefaultMaxFriends;
        if (value.TryGetProperty("maxFriends", out var friends)
            && (friends.ValueKind != JsonValueKind.Number || !friends.TryGetInt32(out maxFriends) || maxFriends is < 1 or > 5000))
            throw new GameException("invalid_config", "social.maxFriends must be a whole number from 1 to 5000.");

        Dictionary<string, GroupTypeRule>? types = null;
        if (value.TryGetProperty("groupTypes", out var groupTypes))
        {
            if (groupTypes.ValueKind != JsonValueKind.Object)
                throw new GameException("invalid_config", "social.groupTypes must be an object.");

            types = new Dictionary<string, GroupTypeRule>(StringComparer.Ordinal);
            foreach (var entry in groupTypes.EnumerateObject())
            {
                if (!ConfigRules.IsValidName(entry.Name))
                    throw new GameException("invalid_config", $"Invalid group type name '{entry.Name}'.");

                types[entry.Name] = new GroupTypeRule(
                    ReadInt(entry.Value, "maxPerUser", 1, 100, 10),
                    ReadInt(entry.Value, "maxMembers", 2, 10_000, 100));
            }

            if (types.Count == 0)
                throw new GameException("invalid_config", "social.groupTypes needs at least one type.");
        }

        return new SocialRules { MaxFriends = maxFriends, GroupTypes = types ?? DefaultTypes };
    }

    private static int ReadInt(JsonElement obj, string name, int min, int max, int fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var element))
            return fallback;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number < min || number > max)
            throw new GameException("invalid_config", $"{name} must be a whole number from {min} to {max}.");

        return number;
    }
}
