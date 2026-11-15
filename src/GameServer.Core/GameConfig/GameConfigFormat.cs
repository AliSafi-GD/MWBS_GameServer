using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GameServer.Core.GameConfig;

/// <summary>
/// The file format: a JSON object with <c>"value"</c> (required, any JSON), <c>"public"</c> (bool, default false) and an
/// optional <c>"$schema"</c>. Fields starting with <c>_</c> are ignored, so a file can carry a comment such as
/// <c>"_note": "..."</c>. Comments (<c>//</c>) and trailing commas are allowed when reading.
/// </summary>
public static class GameConfigFormat
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses the text of <c>&lt;key&gt;.json</c>. Throws <see cref="GameConfigException"/> naming <paramref name="path"/>.</summary>
    public static GameConfigFile Parse(string key, string path, string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, ReadOptions); }
        catch (JsonException ex) { throw new GameConfigException($"{path}: not valid JSON ({ex.Message})"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GameConfigException($"{path}: the file must be a JSON object like {{ \"public\": false, \"value\": {{ ... }} }}.");

            JsonElement? value = null;
            var isPublic = false;
            foreach (var property in root.EnumerateObject())
            {
                var name = property.Name;
                if (name == "$schema" || name.StartsWith('_'))
                    continue;
                if (name == "value")
                    value = property.Value.Clone();
                else if (name == "public")
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new GameConfigException($"{path}: \"public\" must be true or false.");
                    isPublic = property.Value.GetBoolean();
                }
                else
                    throw new GameConfigException(
                        $"{path}: unknown field \"{name}\". Allowed: \"value\", \"public\", \"$schema\"; start a name with _ to leave a comment.");
            }

            if (value is null)
                throw new GameConfigException($"{path}: \"value\" is missing.");
            return new GameConfigFile(key, value.Value, isPublic, path);
        }
    }

    /// <summary>Text of a config file for a database entry: what the admin export puts into the zip.</summary>
    public static string Serialize(ConfigEntry entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("public", entry.IsPublic);
            writer.WritePropertyName("value");
            entry.Value.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
