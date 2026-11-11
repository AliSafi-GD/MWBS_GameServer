using Microsoft.Extensions.Logging;

namespace GameServer.Core.Realtime;

/// <summary>Who is connected and which channels they listen to. Used by social features and by modules (through ISocial/IChat).</summary>
public interface IRealtime
{
    bool IsOnline(string userId);

    /// <summary>The ones among <paramref name="userIds"/> that have an open connection.</summary>
    IReadOnlyCollection<string> OnlineAmong(IEnumerable<string> userIds);

    Task SendToUserAsync(string userId, string frame, CancellationToken ct = default);

    Task SendToUsersAsync(IEnumerable<string> userIds, string frame, CancellationToken ct = default);

    Task SendToChannelAsync(string channel, string frame, CancellationToken ct = default);

    /// <summary>True if any connection of the player has joined the channel.</summary>
    bool IsInChannel(string userId, string channel);

    /// <summary>Stops delivering a channel to all connections of a player (kicked from a group, left a party...).</summary>
    void RemoveUserFromChannel(string userId, string channel);

    /// <summary>Closes every connection of a player (banned, account deleted).</summary>
    Task DisconnectUserAsync(string userId, string reason);

    /// <summary>Raised when a player's last connection closes.</summary>
    event Action<string>? UserWentOffline;
}

public sealed class ConnectionManager : IRealtime
{
    public const int MaxChannelsPerConnection = 20;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<IConnection>> _byUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userOfConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, IConnection>> _channelMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _channelsOfConnection = new(StringComparer.Ordinal);
    private readonly ILogger<ConnectionManager> _log;

    public ConnectionManager(ILogger<ConnectionManager> log) => _log = log;

    public event Action<string>? UserWentOffline;

    // ---- connections ----------------------------------------------------------------------------------------

    public int CountForUser(string userId)
    {
        lock (_gate)
            return _byUser.TryGetValue(userId, out var list) ? list.Count : 0;
    }

    public void Register(string userId, IConnection connection)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var list))
                _byUser[userId] = list = new List<IConnection>();
            list.Add(connection);
            _userOfConnection[connection.Id] = userId;
        }
    }

    public void Unregister(IConnection connection)
    {
        string? offlineUser = null;

        lock (_gate)
        {
            if (!_userOfConnection.Remove(connection.Id, out var userId))
                return;

            if (_channelsOfConnection.Remove(connection.Id, out var channels))
            {
                foreach (var channel in channels)
                    RemoveFromChannelLocked(channel, connection.Id);
            }

            if (_byUser.TryGetValue(userId, out var list))
            {
                list.RemoveAll(c => c.Id == connection.Id);
                if (list.Count == 0)
                {
                    _byUser.Remove(userId);
                    offlineUser = userId;
                }
            }
        }

        if (offlineUser is not null)
        {
            try
            {
                UserWentOffline?.Invoke(offlineUser);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A handler of UserWentOffline failed.");
            }
        }
    }

    /// <summary>Open connections and distinct online players (for the admin panel).</summary>
    public (int Connections, int Users) Counts()
    {
        lock (_gate)
            return (_userOfConnection.Count, _byUser.Count);
    }

    public IReadOnlyList<(string UserId, int Connections)> OnlineUsers(int limit)
    {
        lock (_gate)
            return _byUser.Take(Math.Max(limit, 0)).Select(kv => (kv.Key, kv.Value.Count)).ToList();
    }

    public bool IsOnline(string userId)
    {
        lock (_gate)
            return _byUser.ContainsKey(userId);
    }

    public IReadOnlyCollection<string> OnlineAmong(IEnumerable<string> userIds)
    {
        lock (_gate)
            return userIds.Distinct().Where(id => _byUser.ContainsKey(id)).ToArray();
    }

    public async Task DisconnectUserAsync(string userId, string reason)
    {
        IConnection[] connections;
        lock (_gate)
            connections = _byUser.TryGetValue(userId, out var list) ? list.ToArray() : Array.Empty<IConnection>();

        foreach (var connection in connections)
        {
            try
            {
                await connection.CloseAsync(reason);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Closing connection {Id} failed.", connection.Id);
            }
        }
    }

    // ---- channels -------------------------------------------------------------------------------------------

    /// <summary>Returns false when the connection is not signed in or already listens to too many channels.</summary>
    public bool Join(IConnection connection, string channel)
    {
        lock (_gate)
        {
            if (!_userOfConnection.ContainsKey(connection.Id))
                return false;

            if (!_channelsOfConnection.TryGetValue(connection.Id, out var joined))
                _channelsOfConnection[connection.Id] = joined = new HashSet<string>(StringComparer.Ordinal);
            if (!joined.Contains(channel) && joined.Count >= MaxChannelsPerConnection)
                return false;

            if (!_channelMembers.TryGetValue(channel, out var members))
                _channelMembers[channel] = members = new Dictionary<string, IConnection>(StringComparer.Ordinal);

            members[connection.Id] = connection;
            joined.Add(channel);
            return true;
        }
    }

    public void Leave(IConnection connection, string channel)
    {
        lock (_gate)
        {
            RemoveFromChannelLocked(channel, connection.Id);
            if (_channelsOfConnection.TryGetValue(connection.Id, out var joined))
                joined.Remove(channel);
        }
    }

    public void RemoveUserFromChannel(string userId, string channel)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var list))
                return;

            foreach (var connection in list)
            {
                RemoveFromChannelLocked(channel, connection.Id);
                if (_channelsOfConnection.TryGetValue(connection.Id, out var joined))
                    joined.Remove(channel);
            }
        }
    }

    public bool IsInChannel(string userId, string channel)
    {
        lock (_gate)
        {
            return _channelMembers.TryGetValue(channel, out var members)
                   && _byUser.TryGetValue(userId, out var list)
                   && list.Any(c => members.ContainsKey(c.Id));
        }
    }

    private void RemoveFromChannelLocked(string channel, string connectionId)
    {
        if (_channelMembers.TryGetValue(channel, out var members) && members.Remove(connectionId) && members.Count == 0)
            _channelMembers.Remove(channel);
    }

    // ---- sending --------------------------------------------------------------------------------------------

    public Task SendToUserAsync(string userId, string frame, CancellationToken ct = default)
    {
        IConnection[] targets;
        lock (_gate)
            targets = _byUser.TryGetValue(userId, out var list) ? list.ToArray() : Array.Empty<IConnection>();
        return SendManyAsync(targets, frame, ct);
    }

    public Task SendToUsersAsync(IEnumerable<string> userIds, string frame, CancellationToken ct = default)
    {
        IConnection[] targets;
        lock (_gate)
        {
            targets = userIds.Distinct()
                .SelectMany(id => _byUser.TryGetValue(id, out var list) ? list : new List<IConnection>())
                .ToArray();
        }

        return SendManyAsync(targets, frame, ct);
    }

    /// <summary>Sends to every signed-in player (server-wide events such as broadcast notifications).</summary>
    public Task SendToAllAsync(string frame, CancellationToken ct = default)
    {
        IConnection[] targets;
        lock (_gate)
            targets = _byUser.Values.SelectMany(list => list).ToArray();
        return SendManyAsync(targets, frame, ct);
    }

    public Task SendToChannelAsync(string channel, string frame, CancellationToken ct = default)
    {
        IConnection[] targets;
        lock (_gate)
            targets = _channelMembers.TryGetValue(channel, out var members) ? members.Values.ToArray() : Array.Empty<IConnection>();
        return SendManyAsync(targets, frame, ct);
    }

    private Task SendManyAsync(IConnection[] targets, string frame, CancellationToken ct) => targets.Length switch
    {
        0 => Task.CompletedTask,
        1 => SendAsync(targets[0], frame, ct),
        _ => Task.WhenAll(targets.Select(connection => SendAsync(connection, frame, ct)))
    };

    /// <summary>
    /// Sends to one connection without ever throwing. A connection that cannot take the message within a few
    /// seconds is closed, so one slow client cannot hold up everyone else in a channel.
    /// </summary>
    public async Task SendAsync(IConnection connection, string frame, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            await connection.SendAsync(frame, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (for example the HTTP request ended); the connection itself is fine.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Sending to connection {Id} failed; closing it.", connection.Id);
            _ = connection.CloseAsync("send_failed");
        }
    }
}
