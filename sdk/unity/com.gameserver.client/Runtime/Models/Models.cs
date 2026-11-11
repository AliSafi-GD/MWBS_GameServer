using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // ---- account ----------------------------------------------------------------------------------------------

    public class IdentityInfo
    {
        public string Provider { get; set; }
        public string Label { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class Account
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public string Lang { get; set; }
        public string Timezone { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public List<IdentityInfo> Identities { get; set; }
    }

    /// <summary>Send only the fields you want to change.</summary>
    public class ProfileUpdate
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public string Lang { get; set; }
        public string Timezone { get; set; }
    }

    public class PublicProfile
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class VersionCheck
    {
        /// <summary>"ok", "update_available" or "update_required".</summary>
        public string Status { get; set; }
        public string Latest { get; set; }
        public string Minimum { get; set; }
        public string Url { get; set; }
    }

    // ---- economy ----------------------------------------------------------------------------------------------

    /// <summary>Currencies and items, as a reward or a price.</summary>
    public class Bundle
    {
        public Dictionary<string, long> Wallet { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Items { get; set; } = new Dictionary<string, long>();
    }

    public class EconomyState
    {
        public Dictionary<string, long> Wallet { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Items { get; set; } = new Dictionary<string, long>();
    }

    // ---- shop -------------------------------------------------------------------------------------------------

    public class PurchaseLimit
    {
        /// <summary>"day", "week", "month" or "lifetime".</summary>
        public string Per { get; set; }
        public int Count { get; set; }
    }

    public class ShopProduct
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public Bundle Cost { get; set; }
        public Bundle Gives { get; set; }
        public PurchaseLimit Limit { get; set; }
        /// <summary>How many more times this player can buy it, or null when there is no limit.</summary>
        public int? Remaining { get; set; }
        /// <summary>False for products announced but not yet on sale (or already past their end date).</summary>
        public bool Active { get; set; }
        public DateTime? StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
    }

    public class ShopPurchase
    {
        public string ProductId { get; set; }
        public int Quantity { get; set; }
        /// <summary>True when the same requestId was sent before: nothing was charged again.</summary>
        public bool AlreadyApplied { get; set; }
        public EconomyState State { get; set; }
    }

    // ---- progress ---------------------------------------------------------------------------------------------

    public class Goal
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Stat { get; set; }
        public long Target { get; set; }
        public long Progress { get; set; }
        public bool Completed { get; set; }
        public bool Claimed { get; set; }
        public Bundle Reward { get; set; }
        /// <summary>"all", "day", "week" or "month".</summary>
        public string Period { get; set; }
        public DateTime? ResetsUtc { get; set; }
    }

    public class GoalClaim
    {
        public string Id { get; set; }
        public Bundle Reward { get; set; }
        public EconomyState State { get; set; }
    }

    public class DailyStatus
    {
        public int Streak { get; set; }
        public bool ClaimedToday { get; set; }
        public int NextDay { get; set; }
        public List<Bundle> Rewards { get; set; }
        public DateTime NextClaimUtc { get; set; }
        public int UtcOffsetMinutes { get; set; }
    }

    public class DailyClaim
    {
        public int Day { get; set; }
        public int Streak { get; set; }
        public Bundle Reward { get; set; }
        public EconomyState State { get; set; }
    }

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

    // ---- friends, groups, parties -----------------------------------------------------------------------------

    public static class FriendStates
    {
        public const string Friend = "friend";
        public const string InviteSent = "invite_sent";
        public const string InviteReceived = "invite_received";
        public const string Blocked = "blocked";
    }

    public class Friend
    {
        public PublicProfile Profile { get; set; }
        /// <summary>See <see cref="FriendStates"/>.</summary>
        public string State { get; set; }
        public bool Online { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class FriendResult
    {
        public string UserId { get; set; }
        public string State { get; set; }
    }

    public class Group
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Avatar { get; set; }
        public string Type { get; set; }
        public bool Open { get; set; }
        public int MaxMembers { get; set; }
        public int MemberCount { get; set; }
        public string CreatorId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class GroupMember
    {
        public PublicProfile Profile { get; set; }
        /// <summary>"owner", "admin", "member", "requested", "invited" or "banned".</summary>
        public string Role { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class MyGroup
    {
        public Group Group { get; set; }
        public string Role { get; set; }
    }

    public class NewGroup
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Avatar { get; set; }
        /// <summary>A type from the server's "social" config; default types are "clan" and "group".</summary>
        public string Type { get; set; }
        public bool? Open { get; set; }
        public int? MaxMembers { get; set; }
    }

    /// <summary>Send only the fields you want to change.</summary>
    public class GroupChanges
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Avatar { get; set; }
        public bool? Open { get; set; }
        public int? MaxMembers { get; set; }
    }

    public class PartyMember
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
    }

    public class Party
    {
        public string Id { get; set; }
        public string LeaderId { get; set; }
        public bool Open { get; set; }
        public int MaxSize { get; set; }
        public List<PartyMember> Members { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    // ---- chat -------------------------------------------------------------------------------------------------

    public class ChatMessage
    {
        /// <summary>Null for messages that are not stored (party chat).</summary>
        public long? Id { get; set; }
        public string Channel { get; set; }
        /// <summary>Null for system messages.</summary>
        public string SenderId { get; set; }
        public string SenderName { get; set; }
        public string Text { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Builds channel names: "room:lobby", "group:&lt;id&gt;", "party:&lt;id&gt;", "dm:&lt;userId&gt;".</summary>
    public static class Channels
    {
        public static string Room(string name) => "room:" + name;
        public static string Group(string groupId) => "group:" + groupId;
        public static string Party(string partyId) => "party:" + partyId;
        public static string Direct(string otherUserId) => "dm:" + otherUserId;
    }

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
