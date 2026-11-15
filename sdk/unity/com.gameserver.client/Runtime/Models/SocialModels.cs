using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
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
}
