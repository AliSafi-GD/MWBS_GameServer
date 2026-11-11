using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServer.Core.Realtime;

/// <summary>
/// The message format shared by all transports: <c>{ "type": "...", "id": "...", "data": { ... } }</c>.
/// Clients put their own <c>id</c> on a request and get it back on the reply; server events have no id.
/// </summary>
public static class Frames
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Build(string type, string? id = null, object? data = null) =>
        JsonSerializer.Serialize(new Envelope(type, id, data), Json);

    private sealed record Envelope(string Type, string? Id, object? Data);
}
