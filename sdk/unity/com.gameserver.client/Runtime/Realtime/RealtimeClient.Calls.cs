using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // Chat channels, the matchmaker and matches over the live connection.
    public sealed partial class RealtimeClient
    {
        // ---- chat ---------------------------------------------------------------------------------------------

        /// <summary>Joins a channel to receive its messages (see <see cref="Channels"/>). Rejoined automatically after a reconnect.</summary>
        public async Task<string> JoinChannelAsync(string channel, CancellationToken ct = default(CancellationToken))
        {
            var joined = await RequestAsync("channel.join", new { channel }, "channel.joined", d => (string)d["channel"], ct);
            _channels[joined ?? channel] = true;
            return joined;
        }

        public async Task LeaveChannelAsync(string channel, CancellationToken ct = default(CancellationToken))
        {
            var left = await RequestAsync("channel.leave", new { channel }, "channel.left", d => (string)d["channel"], ct);
            bool ignored;
            _channels.TryRemove(left ?? channel, out ignored);
            _channels.TryRemove(channel, out ignored);
        }

        /// <summary>Sends a message. Rooms and groups need a prior <see cref="JoinChannelAsync"/>; direct messages do not.</summary>
        public Task<ChatMessage> SendMessageAsync(string channel, string text, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("channel.send", new { channel, text }, "channel.sent", d => d.To<ChatMessage>(), ct);

        public Task<List<ChatMessage>> ChannelHistoryAsync(string channel, int limit = 50, long? before = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("channel.history", new { channel, limit, before }, "channel.history", d => d["items"].To<List<ChatMessage>>(), ct);

        // ---- matchmaker and matches ---------------------------------------------------------------------------

        /// <summary>
        /// Joins the matchmaking queue of a mode. When enough players are found you get <see cref="MatchFound"/>.
        /// <paramref name="properties"/> are the rule values the mode asks for (for example region or skill).
        /// Pass <paramref name="partyId"/> to queue your whole party.
        /// </summary>
        public Task<Ticket> AddToMatchmakerAsync(string mode, Dictionary<string, object> properties = null, string partyId = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("matchmaker.add", new { mode, properties, partyId }, "matchmaker.ticket", d => d.To<Ticket>(), ct);

        public Task<bool> RemoveFromMatchmakerAsync(string ticketId, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("matchmaker.remove", new { ticketId }, "matchmaker.removed", d => (bool)d["removed"], ct);

        /// <summary>Creates a match yourself (only for modes that allow it) and returns how to connect.</summary>
        public Task<MatchCreated> CreateMatchAsync(string mode, string label = null, string parameters = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.create", new { mode, label, parameters }, "match.created", d => d.To<MatchCreated>(), ct);

        public Task<MatchConnectInfo> JoinMatchAsync(string matchId, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.join", new { matchId }, "match.joined", d => d.To<MatchConnectInfo>(), ct);

        public Task<List<MatchInfo>> ListMatchesAsync(string mode = null, int? limit = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.list", new { mode, limit }, "match.list", d => d["items"].To<List<MatchInfo>>(), ct);
    }
}
