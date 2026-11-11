using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Realtime;
using GameServer.Core.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Matches;

/// <summary>What a client sees of its queue entry.</summary>
public sealed record TicketView(string Id, string Mode, IReadOnlyList<string> UserIds, string? PartyId, DateTime CreatedUtc);

/// <summary>
/// Groups waiting players into matches. A ticket is one player or a whole party. The result is announced with the
/// realtime message <c>matchmaker.matched</c>, which carries everything the client needs to connect to the match with
/// whatever protocol the mode uses.
/// </summary>
public sealed class Matchmaker
{
    private sealed class Ticket
    {
        public required string Id { get; init; }
        public required MatchMode Mode { get; init; }
        public required string OwnerId { get; init; }
        public required string[] UserIds { get; init; }
        public required Dictionary<string, object> Properties { get; init; }
        public string? PartyId { get; init; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public TicketView View() => new(Id, Mode.Name, UserIds, PartyId, CreatedUtc);
    }

    private readonly object _gate = new();
    private readonly List<Ticket> _tickets = new();
    private readonly MatchModeProvider _modes;
    private readonly MatchService _matches;
    private readonly ConnectionManager _realtime;
    private readonly PartyService _parties;
    private readonly IUserStore _users;
    private readonly MatchOptions _options;
    private readonly ILogger<Matchmaker> _log;

    public Matchmaker(
        MatchModeProvider modes, MatchService matches, ConnectionManager realtime, PartyService parties, IUserStore users,
        IOptions<MatchOptions> options, ILogger<Matchmaker> log)
    {
        _modes = modes;
        _matches = matches;
        _realtime = realtime;
        _parties = parties;
        _users = users;
        _options = options.Value;
        _log = log;

        _realtime.UserWentOffline += userId => _ = RemoveForUserAsync(userId, "offline");
    }

    // ---- queue ----------------------------------------------------------------------------------------------

    public async Task<TicketView> AddAsync(
        string userId, string modeName, IReadOnlyDictionary<string, JsonElement>? properties, string? partyId, CancellationToken ct)
    {
        var mode = await _modes.RequireAsync(modeName, ct);
        if (!mode.Matchmaking)
            throw new GameException("matchmaking_disabled", "This mode does not use the matchmaker.");
        if (!_realtime.IsOnline(userId))
            throw new GameException("not_online", "Connect to the realtime socket before joining the matchmaker.");

        var userIds = new[] { userId };
        if (!string.IsNullOrEmpty(partyId))
        {
            var party = _parties.Mine(userId);
            if (party is null || party.Id != partyId)
                throw new GameException("party_not_found", "You are not in that party.");
            if (party.LeaderId != userId)
                throw new GameException("not_party_leader", "Only the party leader can start matchmaking.");
            if (party.Members.Count > mode.MaxPlayers)
                throw new GameException("party_too_large", "The party is bigger than this mode allows.");

            userIds = party.Members.Select(m => m.UserId).ToArray();
            if (_realtime.OnlineAmong(userIds).Count != userIds.Length)
                throw new GameException("not_online", "Every party member must be online.");
        }

        var props = ReadProperties(mode, properties);

        Ticket ticket;
        lock (_gate)
        {
            foreach (var id in userIds)
            {
                var existing = _tickets.Where(t => t.UserIds.Contains(id)).ToList();
                if (existing.Any(t => t.Mode.Name == mode.Name))
                    throw new GameException("already_queued", "Already waiting in this queue.");
                if (existing.Count >= _options.MaxTicketsPerUser)
                    throw new GameException("too_many_tickets", "Too many matchmaker queues at the same time.");
            }

            ticket = new Ticket
            {
                Id = Guid.NewGuid().ToString("N"),
                Mode = mode,
                OwnerId = userId,
                UserIds = userIds,
                Properties = props,
                PartyId = string.IsNullOrEmpty(partyId) ? null : partyId
            };
            _tickets.Add(ticket);
        }

        return ticket.View();
    }

    private static Dictionary<string, object> ReadProperties(MatchMode mode, IReadOnlyDictionary<string, JsonElement>? input)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (input is not null)
        {
            if (input.Count > 20)
                throw new GameException("invalid_request", "Too many matchmaker properties.");

            foreach (var (name, value) in input)
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                    result[name] = number;
                else if (value.ValueKind == JsonValueKind.String)
                    {
                    var text = value.GetString() ?? "";
                    result[name] = text.Length > 100 ? text[..100] : text;
                }
                else
                    throw new GameException("invalid_request", $"Property '{name}' must be a number or a string.");
            }
        }

        foreach (var (name, rule) in mode.Rules)
        {
            if (!result.TryGetValue(name, out var v))
                throw new GameException("invalid_request", $"Property '{name}' is required for this mode.");
            if (!rule.Equal && v is not double)
                throw new GameException("invalid_request", $"Property '{name}' must be a number.");
        }

        return result;
    }

    public async Task<bool> RemoveAsync(string userId, string ticketId)
    {
        Ticket? removed;
        lock (_gate)
        {
            removed = _tickets.FirstOrDefault(t => t.Id == ticketId && t.UserIds.Contains(userId));
            if (removed is not null)
                _tickets.Remove(removed);
        }

        if (removed is null)
            return false;

        await NotifyAsync(removed, "matchmaker.expired", new { ticketId = removed.Id, reason = "cancelled" }, except: userId);
        return true;
    }

    public IReadOnlyList<TicketView> Mine(string userId)
    {
        lock (_gate)
            return _tickets.Where(t => t.UserIds.Contains(userId)).Select(t => t.View()).ToArray();
    }

    public IReadOnlyDictionary<string, int> CountsByMode()
    {
        lock (_gate)
            return _tickets.GroupBy(t => t.Mode.Name).ToDictionary(g => g.Key, g => g.Sum(t => t.UserIds.Length));
    }

    private async Task RemoveForUserAsync(string userId, string reason)
    {
        try
        {
            List<Ticket> removed;
            lock (_gate)
            {
                removed = _tickets.Where(t => t.UserIds.Contains(userId)).ToList();
                foreach (var t in removed)
                    _tickets.Remove(t);
            }

            foreach (var ticket in removed)
                await NotifyAsync(ticket, "matchmaker.expired", new { ticketId = ticket.Id, reason }, except: userId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Removing the tickets of an offline player failed.");
        }
    }

    private Task NotifyAsync(Ticket ticket, string type, object data, string? except = null) =>
        _realtime.SendToUsersAsync(ticket.UserIds.Where(u => u != except), Frames.Build(type, null, data));

    // ---- loop -----------------------------------------------------------------------------------------------

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(_options.MatchmakerIntervalMs, 100)));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "A matchmaker tick failed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = new List<Ticket>();
        var formed = new List<(MatchMode Mode, List<Ticket> Group)>();

        lock (_gate)
        {
            foreach (var t in _tickets.Where(t => (now - t.CreatedUtc).TotalSeconds >= t.Mode.TicketTimeoutSeconds).ToList())
            {
                _tickets.Remove(t);
                expired.Add(t);
            }

            foreach (var byMode in _tickets.GroupBy(t => t.Mode.Name).ToList())
            {
                var mode = byMode.First().Mode;
                var queue = byMode.OrderBy(t => t.CreatedUtc).ToList();
                var used = new HashSet<Ticket>();

                foreach (var seed in queue)
                {
                    if (used.Contains(seed))
                        continue;

                    var group = new List<Ticket> { seed };
                    var total = seed.UserIds.Length;

                    foreach (var candidate in queue)
                    {
                        if (total >= mode.MaxPlayers)
                            break;
                        if (used.Contains(candidate) || ReferenceEquals(candidate, seed))
                            continue;
                        if (total + candidate.UserIds.Length > mode.MaxPlayers)
                            continue;
                        if (group.All(g => Compatible(mode, g, candidate, now)))
                        {
                            group.Add(candidate);
                            total += candidate.UserIds.Length;
                        }
                    }

                    var waited = (now - seed.CreatedUtc).TotalSeconds;
                    if (total == mode.MaxPlayers || (total >= mode.MinPlayers && waited >= mode.FillSeconds))
                    {
                        foreach (var t in group)
                            used.Add(t);
                        formed.Add((mode, group));
                    }
                }
            }

            foreach (var (_, group) in formed)
                foreach (var t in group)
                    _tickets.Remove(t);
        }

        foreach (var ticket in expired)
            await NotifyAsync(ticket, "matchmaker.expired", new { ticketId = ticket.Id, reason = "timeout" });

        foreach (var (mode, group) in formed)
            await CreateMatchAsync(mode, group, ct);
    }

    private static bool Compatible(MatchMode mode, Ticket a, Ticket b, DateTime now)
    {
        var waited = Math.Max((now - a.CreatedUtc).TotalSeconds, (now - b.CreatedUtc).TotalSeconds);

        foreach (var (name, rule) in mode.Rules)
        {
            if (!a.Properties.TryGetValue(name, out var x) || !b.Properties.TryGetValue(name, out var y))
                return false;

            if (rule.Equal)
            {
                if (!Equals(x, y))
                    return false;
            }
            else
            {
                if (x is not double dx || y is not double dy)
                    return false;
                if (Math.Abs(dx - dy) > rule.Range + rule.ExpandPerSecond * waited)
                    return false;
            }
        }

        return true;
    }

    private async Task CreateMatchAsync(MatchMode mode, List<Ticket> group, CancellationToken ct)
    {
        var userIds = group.SelectMany(t => t.UserIds).Distinct().ToArray();

        try
        {
            var profiles = (await _users.GetPublicAsync(userIds, ct)).ToDictionary(p => p.Id, p => p.Username);

            var match = _matches.CreateInternal(mode, null, null, joinable: false);
            var roster = new List<object>();
            foreach (var userId in userIds)
            {
                var player = match.AddPlayer(userId, profiles.GetValueOrDefault(userId, userId));
                if (player is not null)
                    roster.Add(new { userId = player.UserId, username = player.Username, slot = player.Slot });
            }

            foreach (var ticket in group)
            {
                foreach (var userId in ticket.UserIds)
                {
                    var info = _matches.IssueToken(match, userId);
                    await _realtime.SendToUserAsync(userId, Frames.Build("matchmaker.matched", null, new
                    {
                        ticketId = ticket.Id,
                        matchId = info.MatchId,
                        mode = info.Mode,
                        transport = info.Transport,
                        token = info.Token,
                        expiresUtc = info.ExpiresUtc,
                        endpoint = info.Endpoint,
                        players = roster
                    }), ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Creating a matchmaker match for mode {Mode} failed; the players are told to queue again.", mode.Name);
            foreach (var ticket in group)
                await NotifyAsync(ticket, "matchmaker.expired", new { ticketId = ticket.Id, reason = "match_failed" });
        }
    }
}
