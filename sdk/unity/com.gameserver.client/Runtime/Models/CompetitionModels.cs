using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // ---- leaderboards and tournaments -------------------------------------------------------------------------

    public class BoardEntry
    {
        public int Rank { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public long Score { get; set; }
    }

    public class Board
    {
        public string Id { get; set; }
        public string Period { get; set; }
        public DateTime? PeriodEndsUtc { get; set; }
        public string Sort { get; set; }
        public List<BoardEntry> Entries { get; set; }
        /// <summary>The player's own row, or null when they have no score.</summary>
        public BoardEntry Me { get; set; }
    }

    public class TournamentReward
    {
        public int FromRank { get; set; }
        public int ToRank { get; set; }
        public Dictionary<string, long> Wallet { get; set; }
        public Dictionary<string, long> Items { get; set; }
    }

    public class Tournament
    {
        public string Id { get; set; }
        /// <summary>"upcoming", "active" or "ended".</summary>
        public string Status { get; set; }
        public JToken Name { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Sort { get; set; }
        public Dictionary<string, long> EntryFee { get; set; }
        public List<TournamentReward> Rewards { get; set; }
        public bool Joined { get; set; }
        public bool Claimed { get; set; }
    }

    public class TournamentDetail
    {
        public Tournament Tournament { get; set; }
        public Board Board { get; set; }
    }

    public class TournamentClaim
    {
        public int Rank { get; set; }
        public long Score { get; set; }
        public Dictionary<string, long> Wallet { get; set; }
        public Dictionary<string, long> Items { get; set; }
    }

    // ---- notifications ----------------------------------------------------------------------------------------

    public class Notification
    {
        public long Id { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        /// <summary>Null when there is nothing to claim. Shape: { wallet: {...}, items: {...} }.</summary>
        public Bundle Reward { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public bool Read { get; set; }
        public bool Claimed { get; set; }
    }

    public class NotificationClaim
    {
        public Bundle Reward { get; set; }
        public Dictionary<string, long> Wallet { get; set; }
        public Dictionary<string, long> Items { get; set; }
    }
}
