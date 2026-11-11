using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>Temporary IStorage so the skeleton runs without a database. Replace with a Postgres implementation.</summary>
public sealed class InMemoryStorage : IStorage
{
    private readonly ConcurrentDictionary<(string UserId, string Key), string> _data = new();

    public Task<T?> ReadAsync<T>(string userId, string key, CancellationToken ct = default)
    {
        if (!_data.TryGetValue((userId, key), out var json))
            return Task.FromResult(default(T));
        return Task.FromResult(JsonSerializer.Deserialize<T>(json));
    }

    public Task WriteAsync<T>(string userId, string key, T value, CancellationToken ct = default)
    {
        _data[(userId, key)] = JsonSerializer.Serialize(value);
        return Task.CompletedTask;
    }
}
