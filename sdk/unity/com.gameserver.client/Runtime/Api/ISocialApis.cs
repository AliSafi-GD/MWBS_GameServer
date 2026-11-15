using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Friends, clans/groups and parties. Their live events (invites, joins) arrive on <see cref="RealtimeClient"/>.</summary>
    public interface ISocialApi
    {
        IFriendsApi Friends { get; }

        IGroupsApi Groups { get; }

        IPartiesApi Parties { get; }

        /// <summary>Which of these players are connected to the realtime server right now (up to 200 ids).</summary>
        Task<List<string>> OnlineAsync(IEnumerable<string> userIds, CancellationToken ct = default(CancellationToken));
    }

    public interface IFriendsApi
    {
        /// <param name="state">One of <see cref="FriendStates"/>, or null for everything except blocked.</param>
        Task<List<Friend>> ListAsync(string state = null, int limit = 100, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Sends an invite, or accepts one that player already sent you. Give a user id or a username.
        /// A player who blocked you looks the same as a normal invite: nothing reaches them.
        /// </summary>
        Task<FriendResult> AddAsync(string userId = null, string username = null, CancellationToken ct = default(CancellationToken));

        /// <summary>Removes a friend, or cancels / declines an invite.</summary>
        Task RemoveAsync(string userId, CancellationToken ct = default(CancellationToken));

        Task BlockAsync(string userId, CancellationToken ct = default(CancellationToken));

        Task UnblockAsync(string userId, CancellationToken ct = default(CancellationToken));
    }

    public interface IGroupsApi
    {
        Task<Group> CreateAsync(NewGroup group, CancellationToken ct = default(CancellationToken));

        Task<List<Group>> SearchAsync(string query = null, string type = null, int limit = 20, CancellationToken ct = default(CancellationToken));

        /// <summary>Groups I belong to, or am invited to / have requested.</summary>
        Task<List<MyGroup>> MineAsync(CancellationToken ct = default(CancellationToken));

        Task<Group> GetAsync(string groupId, CancellationToken ct = default(CancellationToken));

        Task<Group> UpdateAsync(string groupId, GroupChanges changes, CancellationToken ct = default(CancellationToken));

        Task DeleteAsync(string groupId, CancellationToken ct = default(CancellationToken));

        /// <summary>Joins an open group, or sends a join request / accepts an invite. Returns "joined", "requested" and similar.</summary>
        Task<string> JoinAsync(string groupId, CancellationToken ct = default(CancellationToken));

        Task LeaveAsync(string groupId, CancellationToken ct = default(CancellationToken));

        /// <param name="pending">true = join requests and invites (admins only).</param>
        Task<List<GroupMember>> MembersAsync(string groupId, bool pending = false, int limit = 100, CancellationToken ct = default(CancellationToken));

        Task InviteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        /// <summary>Accepts a player's join request.</summary>
        Task AcceptAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        Task KickAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        Task BanAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        Task UnbanAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        Task PromoteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        Task DemoteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));

        /// <summary>The owner hands the group to another member.</summary>
        Task TransferAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Short-lived groups for playing together. Creating or joining one needs an open realtime connection.</summary>
    public interface IPartiesApi
    {
        Task<Party> CreateAsync(bool open = false, int? maxSize = null, CancellationToken ct = default(CancellationToken));

        /// <summary>The party I am in, or null.</summary>
        Task<Party> MineAsync(CancellationToken ct = default(CancellationToken));

        Task<Party> GetAsync(string partyId, CancellationToken ct = default(CancellationToken));

        /// <summary>Joins an open party, or a closed one you were invited to. Error: not_invited.</summary>
        Task<Party> JoinAsync(string partyId, CancellationToken ct = default(CancellationToken));

        Task LeaveAsync(CancellationToken ct = default(CancellationToken));

        Task InviteAsync(string userId, CancellationToken ct = default(CancellationToken));

        Task KickAsync(string userId, CancellationToken ct = default(CancellationToken));

        Task PromoteAsync(string userId, CancellationToken ct = default(CancellationToken));

        /// <summary>The leader closes the party.</summary>
        Task CloseAsync(CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Stored chat history over HTTP. Live chat is on <see cref="RealtimeClient"/>.</summary>
    public interface IChatApi
    {
        /// <summary>Newest first. Pass the oldest id you have as <paramref name="before"/> for the next page.</summary>
        Task<List<ChatMessage>> HistoryAsync(string channel, int limit = 50, long? before = null, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// My direct chats and clans, newest first, each with its last message and the number of unread messages from others
        /// (at most 100). One call at startup is enough; after that count new messages from <see cref="IRealtimeClient.ChatMessageReceived"/>.
        /// </summary>
        Task<List<Conversation>> ConversationsAsync(int limit = 50, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Marks a direct or clan conversation as read up to <paramref name="messageId"/>, on every device. The mark never moves back.
        /// Errors: not_stored (party chat and rooms), not_a_member.
        /// </summary>
        Task MarkReadAsync(string channel, long messageId, CancellationToken ct = default(CancellationToken));
    }
}
