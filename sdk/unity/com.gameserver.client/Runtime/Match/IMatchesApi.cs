using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>
    /// Matchmaker, finding and creating matches over HTTP, and connecting to a match. The same matchmaker actions
    /// exist on <see cref="RealtimeClient"/>; use either. Queueing needs an open realtime connection because the result
    /// ("matchmaker.matched") arrives there.
    /// </summary>
    public interface IMatchesApi
    {
        /// <summary>The server's match modes and which protocol each one uses.</summary>
        Task<MatchModes> ModesAsync(CancellationToken ct = default(CancellationToken));

        Task<Ticket> QueueAsync(string mode, Dictionary<string, object> properties = null, string partyId = null, CancellationToken ct = default(CancellationToken));

        Task<List<Ticket>> TicketsAsync(CancellationToken ct = default(CancellationToken));

        Task<bool> UnqueueAsync(string ticketId, CancellationToken ct = default(CancellationToken));

        /// <summary>Creates a match (only for modes that allow it) and returns how to connect to it.</summary>
        Task<MatchCreated> CreateAsync(string mode, string label = null, string parameters = null, CancellationToken ct = default(CancellationToken));

        Task<List<MatchInfo>> ListAsync(string mode = null, int? limit = null, CancellationToken ct = default(CancellationToken));

        Task<MatchInfo> GetAsync(string matchId, CancellationToken ct = default(CancellationToken));

        /// <summary>Reserves a place in an open match. Then call <see cref="ConnectAsync"/> within a few seconds.</summary>
        Task<MatchConnectInfo> JoinAsync(string matchId, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Connects to the match using the protocol named in <paramref name="info"/> (from <see cref="MatchFound.ToConnectInfo"/>,
        /// <see cref="JoinAsync"/> or a created match). The token is single use and short lived.
        /// </summary>
        Task<MatchConnection> ConnectAsync(MatchConnectInfo info, CancellationToken ct = default(CancellationToken));

        Task<MatchConnection> ConnectAsync(MatchFound found, CancellationToken ct = default(CancellationToken));
    }
}
