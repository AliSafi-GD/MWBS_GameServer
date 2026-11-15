using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // ---- matches ----------------------------------------------------------------------------------------------

    public class MatchMode
    {
        public string Name { get; set; }
        /// <summary>Which protocol this mode's match uses ("websocket", "udp", ...).</summary>
        public string Transport { get; set; }
        public int MinPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int TickRate { get; set; }
        public bool Matchmaking { get; set; }
        public bool AllowClientCreate { get; set; }
    }

    public class MatchModes
    {
        public List<MatchMode> Modes { get; set; }
        public List<JObject> Transports { get; set; }
    }

    public class Ticket
    {
        public string Id { get; set; }
        public string Mode { get; set; }
        public List<string> UserIds { get; set; }
        public string PartyId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class MatchInfo
    {
        public string Id { get; set; }
        public string Mode { get; set; }
        public string Label { get; set; }
        public int Players { get; set; }
        public int Connected { get; set; }
        public int MaxPlayers { get; set; }
        public bool Open { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Everything needed to connect to a match with the protocol its mode uses. The token works once.</summary>
    public class MatchConnectInfo
    {
        public string MatchId { get; set; }
        public string Mode { get; set; }
        public string Transport { get; set; }
        public string Token { get; set; }
        public DateTime ExpiresUtc { get; set; }
        /// <summary>Transport specific: websocket { path }, udp { host, port }.</summary>
        public JObject Endpoint { get; set; }
    }

    public class MatchCreated
    {
        public MatchInfo Match { get; set; }
        public MatchConnectInfo Connect { get; set; }
    }

    public class MatchPlayerInfo
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public int Slot { get; set; }
    }

    /// <summary>The realtime event "matchmaker.matched".</summary>
    public class MatchFound
    {
        public string TicketId { get; set; }
        public string MatchId { get; set; }
        public string Mode { get; set; }
        public string Transport { get; set; }
        public string Token { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public JObject Endpoint { get; set; }
        public List<MatchPlayerInfo> Players { get; set; }

        public MatchConnectInfo ToConnectInfo() => new MatchConnectInfo
        {
            MatchId = MatchId,
            Mode = Mode,
            Transport = Transport,
            Token = Token,
            ExpiresUtc = ExpiresUtc,
            Endpoint = Endpoint
        };
    }

    /// <summary>The realtime event "matchmaker.expired". Reason: "timeout", "cancelled", "match_failed", ...</summary>
    public class TicketExpired
    {
        public string TicketId { get; set; }
        public string Reason { get; set; }
    }
}
