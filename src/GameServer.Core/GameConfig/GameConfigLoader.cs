namespace GameServer.Core.GameConfig;

/// <summary>A folder of config files and a name for it in messages (for example "module HelloGame.Modules").</summary>
public sealed record GameConfigSource(string Label, string Folder);

/// <summary>Reads and validates the config folders. Pure file work: no database, usable from tests.</summary>
public static class GameConfigLoader
{
    /// <summary>
    /// Reads every <c>*.json</c> in each source folder (not sub-folders; names starting with _ are skipped), validates
    /// them, and checks that no key is defined twice. Collects all problems and throws one
    /// <see cref="GameConfigException"/> listing them. Result is sorted by key.
    /// </summary>
    public static IReadOnlyList<GameConfigFile> Load(IEnumerable<GameConfigSource> sources)
    {
        var errors = new List<string>();
        var byKey = new Dictionary<string, GameConfigFile>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (!Directory.Exists(source.Folder))
            {
                errors.Add($"{source.Label}: config folder '{source.Folder}' does not exist.");
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(source.Folder, "*.json").Order(StringComparer.Ordinal))
            {
                var key = Path.GetFileNameWithoutExtension(path);
                if (key.StartsWith('_'))
                    continue; // _notes.json, _template.json: not config

                GameConfigFile file;
                try
                {
                    file = GameConfigFormat.Parse(key, path, File.ReadAllText(path));
                }
                catch (GameConfigException ex)
                {
                    errors.Add(ex.Message);
                    continue;
                }
                catch (IOException ex)
                {
                    errors.Add($"{path}: cannot read the file ({ex.Message})");
                    continue;
                }

                if (GameConfigValidator.Check(file) is { } problem)
                {
                    errors.Add(problem);
                    continue;
                }

                if (byKey.TryGetValue(key, out var other))
                    errors.Add($"Config key '{key}' is defined twice: {other.Path} and {path}. Keep it in one place.");
                else
                    byKey[key] = file;
            }
        }

        if (errors.Count > 0)
            throw new GameConfigException("Game config files are invalid:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));

        return byKey.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }
}
