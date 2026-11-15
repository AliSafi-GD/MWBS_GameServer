using System.Text.Json;

namespace GameServer.Core.GameConfig;

public enum GameConfigAction { Write, Skip }

/// <summary>What startup does with one file, and why (the reason is logged).</summary>
public sealed record GameConfigStep(GameConfigFile File, GameConfigAction Action, string Reason, ConfigEntry? Previous);

/// <summary>The decision logic of the apply step: files + what the database holds + the mode, nothing else. Never deletes.</summary>
public static class GameConfigPlanner
{
    public static IReadOnlyList<GameConfigStep> Plan(
        IEnumerable<GameConfigFile> files, IReadOnlyList<ConfigEntry> existing, GameConfigMode mode)
    {
        var steps = new List<GameConfigStep>();
        if (mode == GameConfigMode.Off)
            return steps;

        var current = existing.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!current.TryGetValue(file.Key, out var entry))
                steps.Add(new GameConfigStep(file, GameConfigAction.Write, "not in the database yet", null));
            else if (mode == GameConfigMode.Seed)
                steps.Add(new GameConfigStep(file, GameConfigAction.Skip, "already in the database (seed mode keeps it)", entry));
            else if (!JsonEquals(entry.Value, file.Value))
                steps.Add(new GameConfigStep(file, GameConfigAction.Write, "value differs from the database", entry));
            else if (entry.IsPublic != file.EffectivePublic)
                steps.Add(new GameConfigStep(file, GameConfigAction.Write, "public flag differs from the database", entry));
            else
                steps.Add(new GameConfigStep(file, GameConfigAction.Skip, "unchanged", entry));
        }
        return steps;
    }

    /// <summary>JSON equality that ignores whitespace and property order (Postgres jsonb reorders properties).</summary>
    public static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var rightCount = 0;
                foreach (var p in b.EnumerateObject())
                {
                    rightCount++;
                    if (!left.TryGetValue(p.Name, out var other) || !JsonEquals(other, p.Value))
                        return false;
                }
                return rightCount == left.Count;
            case JsonValueKind.Array:
                var x = a.EnumerateArray().ToArray();
                var y = b.EnumerateArray().ToArray();
                return x.Length == y.Length && x.Zip(y).All(t => JsonEquals(t.First, t.Second));
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var da) && b.TryGetDecimal(out var db) ? da == db : a.GetRawText() == b.GetRawText();
            default:
                return true; // true/true, false/false, null/null
        }
    }
}
