using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameServer.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Matches;

/// <summary>Owns the running matches, the join tokens and the link between connections and matches.</summary>
public sealed class MatchService : IMatches, IMatchHost
{
    private sealed record Token(string MatchId, string UserId, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<string, MatchInstance> _matches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Token> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MatchInstance> _byConnection = new(StringComparer.Ordinal);
    private readonly MatchModeProvider _modes;
    private readonly ModuleRegistry _registry;
    private readonly IUserStore _users;
    private readonly IServiceProvider _services;
    private readonly MatchOptions _options;
    private readonly ILogger<MatchService> _log;

    public MatchService(
        MatchModeProvider modes, ModuleRegistry registry, IUserStore users, IServiceProvider services,
        IOptions<MatchOptions> options, ILogger<MatchService> log)
    {
        _modes = modes;
        _registry = registry;
        _users = users;
        _services = services;
        _options = options.Value;
        _log = log;
    }

    public MatchModeProvider Modes => _modes;

    // ---- transports (resolved lazily: transports depend on this service as IMatchHost) ------------------------

    public IReadOnlyList<IMatchTransport> Transports => _services.GetServices<IMatchTransport>().ToArray();

    private IMatchTransport ResolveTransport(MatchMode mode)
    {
        var transports = Transports;
        if (transports.Count == 0)
            throw new GameException("match_transports_disabled", "No match transport is enabled (GameServer:Match:Transports).");

        if (mode.Transport is null)
            return transports[0];

        return transports.FirstOrDefault(t => string.Equals(t.Name, mode.Transport, StringComparison.OrdinalIgnoreCase))
               ?? throw new GameException("transport_not_enabled",
                   $"Mode '{mode.Name}' uses the '{mode.Transport}' transport, which is not enabled.");
    }

    // ---- IMatches -------------------------------------------------------------------------------------------

    public async Task<MatchInfo> CreateAsync(string mode, string? label = null, string? parametersJson = null, CancellationToken ct = default)
    {
        var definition = await _modes.RequireAsync(mode, ct);
        return CreateInternal(definition, label, parametersJson, joinable: true).Info();
    }

    internal MatchInstance CreateInternal(MatchMode definition, string? label, string? parametersJson, bool joinable)
    {
        if (_matches.Count >= _options.MaxMatches)
            throw new GameException("too_many_matches", "The server is running as many matches as it allows.");

        ResolveTransport(definition); // fail now, not when the first player tries to connect

        var handler = CreateHandler(definition.Handler);
        var id = Guid.NewGuid().ToString("N");
        var instance = new MatchInstance(id, definition, handler, label, parametersJson, joinable, CreateServices(), _log);
        instance.Ended += OnMatchEnded;
        _matches[id] = instance;
        instance.Start();
        return instance;
    }

    private MatchServices CreateServices() => new(
        _services.GetRequiredService<IEconomy>(), _services.GetRequiredService<IProgress>(),
        _services.GetRequiredService<ILeaderboards>(), _services.GetRequiredService<INotifications>(),
        _services.GetRequiredService<IConfig>());

    private MatchHandler CreateHandler(string name)
    {
        if (_registry.TryGetMatchHandler(name, out var factory))
            return factory();
        if (name == "relay")
            return new RelayHandler();
        throw new GameException("match_handler_not_found", $"No match handler named '{name}' is registered by any module.");
    }

    private void OnMatchEnded(MatchInstance match)
    {
        _matches.TryRemove(match.Id, out _);
        foreach (var pair in _byConnection.Where(p => ReferenceEquals(p.Value, match)).ToArray())
            _byConnection.TryRemove(pair.Key, out _);
        foreach (var pair in _tokens.Where(p => p.Value.MatchId == match.Id).ToArray())
            _tokens.TryRemove(pair.Key, out _);
        _log.LogDebug("Match {Id} ({Mode}) ended: {Reason}", match.Id, match.Mode, match.EndReason);
    }

    public async Task<MatchConnectInfo> JoinAsync(string matchId, string userId, CancellationToken ct = default)
    {
        if (!_matches.TryGetValue(matchId, out var match) || match.IsEnded)
            throw new GameException("match_not_found", "Match not found.");
        if (match.IsKicked(userId))
            throw new GameException("kicked_from_match", "You were removed from this match.");

        var player = match.FindPlayer(userId);
        if (player is null)
        {
            if (!match.Joinable)
                throw new GameException("match_not_found", "Match not found.");

            var profile = (await _users.GetPublicAsync(new[] { userId }, ct)).FirstOrDefault()
                          ?? throw new GameException("user_not_found", "User not found.");
            player = match.AddPlayer(userId, profile.Username)
                     ?? throw new GameException("match_full", "The match is full.");
        }

        return IssueToken(match, userId);
    }

    internal MatchConnectInfo IssueToken(MatchInstance match, string userId)
    {
        var transport = ResolveTransport(match.Definition);

        // One token per player and match: a new one replaces the old one.
        foreach (var old in _tokens.Where(p => p.Value.UserId == userId && p.Value.MatchId == match.Id).ToArray())
            _tokens.TryRemove(old.Key, out _);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = DateTime.UtcNow.AddSeconds(Math.Max(_options.TokenSeconds, 5));
        _tokens[token] = new Token(match.Id, userId, expires);

        return new MatchConnectInfo(match.Id, match.Mode, transport.Name, token, expires, transport.Describe());
    }

    public MatchInfo? Get(string matchId) => _matches.TryGetValue(matchId, out var m) ? m.Info() : null;

    public IReadOnlyList<MatchInfo> List(string? mode = null, int limit = 50) =>
        _matches.Values
            .Where(m => !m.IsEnded && (mode is null || m.Mode == mode))
            .Select(m => m.Info())
            .OrderByDescending(i => i.CreatedUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();

    public Task EndAsync(string matchId, string? reason = null, CancellationToken ct = default)
    {
        if (!_matches.TryGetValue(matchId, out var match))
            throw new GameException("match_not_found", "Match not found.");
        match.End(reason ?? "ended_by_server");
        return Task.CompletedTask;
    }

    public int Count => _matches.Count;

    /// <summary>Running matches including those no longer open to join (admin panel).</summary>
    public IReadOnlyList<(MatchInfo Info, IReadOnlyList<MatchPlayer> Players)> Snapshot(string? mode, int limit) =>
        _matches.Values
            .Where(m => !m.IsEnded && (mode is null || m.Mode == mode))
            .OrderByDescending(m => m.CreatedUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(m => (m.Info(), m.Players))
            .ToArray();

    public void EndAll(string reason)
    {
        foreach (var match in _matches.Values)
            match.End(reason);
    }

    public void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var match in _matches.Values)
            match.CheckTimeouts(now);
        foreach (var pair in _tokens.Where(p => p.Value.ExpiresUtc < now).ToArray())
            _tokens.TryRemove(pair.Key, out _);
    }

    // ---- IMatchHost -----------------------------------------------------------------------------------------

    public bool IsTokenValid(string token) =>
        !string.IsNullOrEmpty(token) && _tokens.TryGetValue(token, out var t) && t.ExpiresUtc > DateTime.UtcNow
        && _matches.ContainsKey(t.MatchId);

    public async Task<bool> OnConnectedAsync(IMatchConnection connection, string token)
    {
        if (string.IsNullOrEmpty(token) || !_tokens.TryRemove(token, out var t) || t.ExpiresUtc <= DateTime.UtcNow)
            return false;
        if (!_matches.TryGetValue(t.MatchId, out var match) || match.IsEnded)
            return false;

        var player = match.FindPlayer(t.UserId);
        if (player is null)
            return false;

        _byConnection[connection.Id] = match;
        if (await match.ConnectAsync(connection, player))
            return true;

        _byConnection.TryRemove(connection.Id, out _);
        return false;
    }

    public Task OnMessageAsync(IMatchConnection connection, ReadOnlyMemory<byte> data)
    {
        if (data.Length <= _options.MaxMessageBytes && _byConnection.TryGetValue(connection.Id, out var match))
            match.Message(connection, data);
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(IMatchConnection connection)
    {
        if (_byConnection.TryRemove(connection.Id, out var match))
            match.Disconnected(connection);
        return Task.CompletedTask;
    }
}
