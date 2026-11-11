using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Matches;

/// <summary>A matchmaker rule for one ticket property.</summary>
/// <param name="Equal">true: both tickets must have the same value. false: numbers must be within a range.</param>
/// <param name="Range">Allowed difference of a numeric property.</param>
/// <param name="ExpandPerSecond">The allowed difference grows by this much for every second the older ticket has waited.</param>
public sealed record MatchRule(bool Equal, double Range, double ExpandPerSecond);

public sealed record MatchMode(
    string Name,
    string Handler,
    string? Transport,
    int MinPlayers,
    int MaxPlayers,
    int TickRate,
    bool AllowClientCreate,
    bool Matchmaking,
    int FillSeconds,
    int TicketTimeoutSeconds,
    int JoinTimeoutSeconds,
    int EmptyTimeoutSeconds,
    IReadOnlyDictionary<string, MatchRule> Rules);

/// <summary>
/// The "matches" config key:
/// <code>
/// { "modes": { "duel": { "handler": "duel", "transport": "udp", "minPlayers": 2, "maxPlayers": 2, "tickRate": 20,
///     "rules": { "region": "equal", "skill": { "range": 100, "expandPerSecond": 10 } } } } }
/// </code>
/// Everything except the mode name is optional.
/// </summary>
public static class MatchModes
{
    public static IReadOnlyDictionary<string, MatchMode> From(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } v ? Parse(v) : new Dictionary<string, MatchMode>();

    /// <summary>Throws GameException("invalid_config") for an invalid value. Also used to validate config writes.</summary>
    public static IReadOnlyDictionary<string, MatchMode> Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", "matches must be an object.");

        var result = new Dictionary<string, MatchMode>(StringComparer.Ordinal);
        if (!value.TryGetProperty("modes", out var modes))
            return result;
        if (modes.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", "matches.modes must be an object.");

        foreach (var entry in modes.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(entry.Name))
                throw new GameException("invalid_config", $"Invalid mode name '{entry.Name}'.");
            var m = entry.Value;
            if (m.ValueKind != JsonValueKind.Object)
                throw new GameException("invalid_config", $"matches.modes.{entry.Name} must be an object.");

            var min = Int(m, "minPlayers", 2, 1, 1000, entry.Name);
            var max = Int(m, "maxPlayers", Math.Max(min, 2), 1, 1000, entry.Name);
            if (max < min)
                throw new GameException("invalid_config", $"matches.modes.{entry.Name}: maxPlayers must be at least minPlayers.");

            var handler = Str(m, "handler") ?? entry.Name;
            if (!ConfigRules.IsValidName(handler))
                throw new GameException("invalid_config", $"matches.modes.{entry.Name}.handler is not a valid name.");

            var transport = Str(m, "transport")?.ToLowerInvariant();

            var rules = new Dictionary<string, MatchRule>(StringComparer.Ordinal);
            if (m.TryGetProperty("rules", out var ruleElement))
            {
                if (ruleElement.ValueKind != JsonValueKind.Object)
                    throw new GameException("invalid_config", $"matches.modes.{entry.Name}.rules must be an object.");

                foreach (var rule in ruleElement.EnumerateObject())
                {
                    if (rule.Value.ValueKind == JsonValueKind.String && rule.Value.GetString() == "equal")
                        rules[rule.Name] = new MatchRule(true, 0, 0);
                    else if (rule.Value.ValueKind == JsonValueKind.Object)
                        rules[rule.Name] = new MatchRule(false, Num(rule.Value, "range", 0, entry.Name), Num(rule.Value, "expandPerSecond", 0, entry.Name));
                    else
                        throw new GameException("invalid_config",
                            $"matches.modes.{entry.Name}.rules.{rule.Name} must be \"equal\" or {{ \"range\": n, \"expandPerSecond\": n }}.");
                }
            }

            result[entry.Name] = new MatchMode(
                entry.Name, handler, transport, min, max,
                Int(m, "tickRate", 0, 0, 60, entry.Name),
                Bool(m, "allowClientCreate", false, entry.Name),
                Bool(m, "matchmaking", true, entry.Name),
                Int(m, "fillSeconds", 5, 0, 600, entry.Name),
                Int(m, "ticketTimeoutSeconds", 120, 5, 3600, entry.Name),
                Int(m, "joinTimeoutSeconds", 30, 5, 600, entry.Name),
                Int(m, "emptyTimeoutSeconds", 30, 0, 3600, entry.Name),
                rules);
        }

        return result;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int Int(JsonElement obj, string name, int fallback, int min, int max, string mode)
    {
        if (!obj.TryGetProperty(name, out var e))
            return fallback;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n) || n < min || n > max)
            throw new GameException("invalid_config", $"matches.modes.{mode}.{name} must be a whole number from {min} to {max}.");
        return n;
    }

    private static double Num(JsonElement obj, string name, double fallback, string mode)
    {
        if (!obj.TryGetProperty(name, out var e))
            return fallback;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var n) || n < 0)
            throw new GameException("invalid_config", $"matches.modes.{mode}.rules.{name} must be a number of at least 0.");
        return n;
    }

    private static bool Bool(JsonElement obj, string name, bool fallback, string mode)
    {
        if (!obj.TryGetProperty(name, out var e))
            return fallback;
        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GameException("invalid_config", $"matches.modes.{mode}.{name} must be true or false.");
        return e.GetBoolean();
    }
}

/// <summary>Reads the mode list from config, with a short cache so the matchmaker does not query the database every tick.</summary>
public sealed class MatchModeProvider
{
    private readonly IConfigStore _config;
    private (long At, IReadOnlyDictionary<string, MatchMode> Modes)? _cache;

    public MatchModeProvider(IConfigStore config) => _config = config;

    public async Task<IReadOnlyDictionary<string, MatchMode>> GetAsync(CancellationToken ct = default)
    {
        var now = Environment.TickCount64;
        if (_cache is { } c && now - c.At < 3000)
            return c.Modes;

        var modes = MatchModes.From((await _config.GetAsync(ConfigRules.Matches, ct))?.Value);
        _cache = (now, modes);
        return modes;
    }

    public async Task<MatchMode> RequireAsync(string? mode, CancellationToken ct = default)
    {
        var modes = await GetAsync(ct);
        if (mode is null || !modes.TryGetValue(mode, out var found))
            throw new GameException("unknown_match_mode", "That match mode is not defined in the \"matches\" config.");
        return found;
    }

    public void Invalidate() => _cache = null;
}
