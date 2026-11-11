namespace GameServer.Abstractions;

/// <summary>Per-user key/value storage (JSON documents), similar in spirit to Nakama's Storage Engine.</summary>
public interface IStorage
{
    Task<T?> ReadAsync<T>(string userId, string key, CancellationToken ct = default);
    Task WriteAsync<T>(string userId, string key, T value, CancellationToken ct = default);
}
