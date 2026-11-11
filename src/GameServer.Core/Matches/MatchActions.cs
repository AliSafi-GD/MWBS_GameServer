using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Matches;

public sealed record MatchmakerRequest(string Mode, Dictionary<string, JsonElement>? Properties = null, string? PartyId = null);
public sealed record CreateMatchRequest(string Mode, string? Label = null, string? Parameters = null);
public sealed record MatchRequest(string MatchId);
public sealed record TicketRequest(string TicketId);
public sealed record MatchListRequest(string? Mode = null, int? Limit = null);

/// <summary>What players may do with matches. Used by both the realtime socket and the REST API, so both run the same hooks.</summary>
public sealed class MatchActions
{
    private readonly HookRunner _hooks;
    private readonly Matchmaker _matchmaker;
    private readonly MatchService _matches;
    private readonly MatchModeProvider _modes;

    public MatchActions(HookRunner hooks, Matchmaker matchmaker, MatchService matches, MatchModeProvider modes)
    {
        _hooks = hooks;
        _matchmaker = matchmaker;
        _matches = matches;
        _modes = modes;
    }

    public Task<TicketView> QueueAsync(string userId, MatchmakerRequest request, CancellationToken ct) =>
        _hooks.RunAsync(HookOps.MatchmakerAdd, userId, request,
            r => _matchmaker.AddAsync(userId, r.Mode, r.Properties, r.PartyId, ct), ct);

    public Task<bool> UnqueueAsync(string userId, string ticketId) => _matchmaker.RemoveAsync(userId, ticketId);

    public IReadOnlyList<TicketView> Tickets(string userId) => _matchmaker.Mine(userId);

    public async Task<object> CreateAsync(string userId, CreateMatchRequest request, CancellationToken ct) =>
        await _hooks.RunAsync(HookOps.MatchesCreate, userId, request, async r =>
        {
            var mode = await _modes.RequireAsync(r.Mode, ct);
            if (!mode.AllowClientCreate)
                throw new GameException("match_create_forbidden", "Players cannot create matches of this mode; use the matchmaker.");

            var match = await _matches.CreateAsync(r.Mode, r.Label, r.Parameters, ct);
            try
            {
                var connect = await _matches.JoinAsync(match.Id, userId, ct);
                return (object)new { match = _matches.Get(match.Id) ?? match, connect };
            }
            catch
            {
                await _matches.EndAsync(match.Id, "creator_could_not_join", ct);
                throw;
            }
        }, ct);

    public Task<MatchConnectInfo> JoinAsync(string userId, MatchRequest request, CancellationToken ct) =>
        _hooks.RunAsync(HookOps.MatchesJoin, userId, request, r => _matches.JoinAsync(r.MatchId, userId, ct), ct);

    public MatchInfo Get(string matchId) =>
        _matches.Get(matchId) ?? throw new GameException("match_not_found", "Match not found.");

    /// <summary>Matches a player can find and join: open ones only.</summary>
    public IReadOnlyList<MatchInfo> List(string? mode, int? limit) =>
        _matches.List(mode, limit ?? 50).Where(m => m.Open).ToArray();

    public async Task<object> ModesAsync(CancellationToken ct)
    {
        var modes = await _modes.GetAsync(ct);
        return new
        {
            modes = modes.Values.Select(m => new
            {
                name = m.Name,
                transport = m.Transport ?? _matches.Transports.FirstOrDefault()?.Name,
                m.MinPlayers,
                m.MaxPlayers,
                m.TickRate,
                m.Matchmaking,
                m.AllowClientCreate
            }),
            transports = _matches.Transports.Select(t => t.Describe())
        };
    }
}
