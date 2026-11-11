using System.Text.Json;

namespace GameServer.Core;

/// <param name="IsPublic">Public keys are sent to every client by GET /v1/config. Others stay on the server.</param>
public sealed record ConfigEntry(string Key, JsonElement Value, bool IsPublic, DateTime UpdatedUtc);

public interface IConfigStore
{
    Task<IReadOnlyList<ConfigEntry>> ListAsync(CancellationToken ct = default);
    Task<ConfigEntry?> GetAsync(string key, CancellationToken ct = default);
    Task<ConfigEntry> SetAsync(string key, JsonElement value, bool isPublic, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
}
