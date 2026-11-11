namespace GameServer.Abstractions;

/// <summary>
/// Read-only access to the server's remote config (edited in the admin panel). Modules can read every key,
/// including server-only ones. Values are cached for a few seconds, so changes apply almost immediately.
/// </summary>
public interface IConfig
{
    /// <summary>Returns the key's JSON value as T (camelCase, case-insensitive), or default if the key does not exist.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
}
