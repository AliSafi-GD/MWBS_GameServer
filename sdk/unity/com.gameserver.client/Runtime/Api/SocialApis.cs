using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Friends, clans/groups and parties. Their live events (invites, joins) arrive on <see cref="RealtimeClient"/>.</summary>
    public sealed class SocialApi
    {
        private readonly ApiClient _api;

        public FriendsApi Friends { get; }
        public GroupsApi Groups { get; }
        public PartiesApi Parties { get; }

        internal SocialApi(ApiClient api)
        {
            _api = api;
            Friends = new FriendsApi(api);
            Groups = new GroupsApi(api);
            Parties = new PartiesApi(api);
        }

        /// <summary>Which of these players are connected to the realtime server right now (up to 200 ids).</summary>
        public async Task<List<string>> OnlineAsync(IEnumerable<string> userIds, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/online", "ids", string.Join(",", userIds)), ct);
            return root.Get<List<string>>("online") ?? new List<string>();
        }
    }

    public sealed class FriendsApi
    {
        private readonly ApiClient _api;

        internal FriendsApi(ApiClient api)
        {
            _api = api;
        }

        /// <param name="state">One of <see cref="FriendStates"/>, or null for everything except blocked.</param>
        public async Task<List<Friend>> ListAsync(string state = null, int limit = 100, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/friends", "state", state, "limit", limit), ct);
            return root.Get<List<Friend>>("friends");
        }

        /// <summary>
        /// Sends an invite, or accepts one that player already sent you. Give a user id or a username.
        /// A player who blocked you looks the same as a normal invite: nothing reaches them.
        /// </summary>
        public Task<FriendResult> AddAsync(string userId = null, string username = null, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<FriendResult>("POST", "/v1/friends", new { userId, username }, true, ct);

        /// <summary>Removes a friend, or cancels / declines an invite.</summary>
        public Task RemoveAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/friends/" + ApiClient.Seg(userId), null, true, ct);

        public Task BlockAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/friends/" + ApiClient.Seg(userId) + "/block", null, true, ct);

        public Task UnblockAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/friends/" + ApiClient.Seg(userId) + "/block", null, true, ct);
    }

    public sealed class GroupsApi
    {
        private readonly ApiClient _api;

        internal GroupsApi(ApiClient api)
        {
            _api = api;
        }

        public async Task<Group> CreateAsync(NewGroup group, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/groups", group, true, ct);
            return root.Get<Group>("group");
        }

        public async Task<List<Group>> SearchAsync(string query = null, string type = null, int limit = 20, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/groups", "q", query, "type", type, "limit", limit), ct);
            return root.Get<List<Group>>("groups");
        }

        /// <summary>Groups I belong to, or am invited to / have requested.</summary>
        public async Task<List<MyGroup>> MineAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/groups/mine", ct);
            return root.Get<List<MyGroup>>("groups");
        }

        public async Task<Group> GetAsync(string groupId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/groups/" + ApiClient.Seg(groupId), ct);
            return root.Get<Group>("group");
        }

        public async Task<Group> UpdateAsync(string groupId, GroupChanges changes, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("PUT", "/v1/groups/" + ApiClient.Seg(groupId), changes, true, ct);
            return root.Get<Group>("group");
        }

        public Task DeleteAsync(string groupId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/groups/" + ApiClient.Seg(groupId), null, true, ct);

        /// <summary>Joins an open group, or sends a join request / accepts an invite. Returns "joined", "requested" and similar.</summary>
        public async Task<string> JoinAsync(string groupId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/groups/" + ApiClient.Seg(groupId) + "/join", null, true, ct);
            return root.Get<string>("status");
        }

        public Task LeaveAsync(string groupId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/groups/" + ApiClient.Seg(groupId) + "/leave", null, true, ct);

        /// <param name="pending">true = join requests and invites (admins only).</param>
        public async Task<List<GroupMember>> MembersAsync(string groupId, bool pending = false, int limit = 100, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/groups/" + ApiClient.Seg(groupId) + "/members",
                "pending", pending, "limit", limit), ct);
            return root.Get<List<GroupMember>>("members");
        }

        public Task InviteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/invite/" + ApiClient.Seg(userId), ct);

        /// <summary>Accepts a player's join request.</summary>
        public Task AcceptAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/members/" + ApiClient.Seg(userId) + "/accept", ct);

        public Task KickAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/members/" + ApiClient.Seg(userId) + "/kick", ct);

        public Task BanAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/members/" + ApiClient.Seg(userId) + "/ban", ct);

        public Task UnbanAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("DELETE", groupId, "/members/" + ApiClient.Seg(userId) + "/ban", ct);

        public Task PromoteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/members/" + ApiClient.Seg(userId) + "/promote", ct);

        public Task DemoteAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/members/" + ApiClient.Seg(userId) + "/demote", ct);

        /// <summary>The owner hands the group to another member.</summary>
        public Task TransferAsync(string groupId, string userId, CancellationToken ct = default(CancellationToken)) =>
            Act("POST", groupId, "/transfer/" + ApiClient.Seg(userId), ct);

        private Task Act(string method, string groupId, string suffix, CancellationToken ct) =>
            _api.SendAsync(method, "/v1/groups/" + ApiClient.Seg(groupId) + suffix, null, true, ct);
    }

    /// <summary>Short-lived groups for playing together. Creating or joining one needs an open realtime connection.</summary>
    public sealed class PartiesApi
    {
        private readonly ApiClient _api;

        internal PartiesApi(ApiClient api)
        {
            _api = api;
        }

        public async Task<Party> CreateAsync(bool open = false, int? maxSize = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/parties", new { open, maxSize }, true, ct);
            return root.Get<Party>("party");
        }

        /// <summary>The party I am in, or null.</summary>
        public async Task<Party> MineAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/parties/mine", ct);
            return root.Get<Party>("party");
        }

        public async Task<Party> GetAsync(string partyId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/parties/" + ApiClient.Seg(partyId), ct);
            return root.Get<Party>("party");
        }

        /// <summary>Joins an open party, or a closed one you were invited to. Error: not_invited.</summary>
        public async Task<Party> JoinAsync(string partyId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/parties/" + ApiClient.Seg(partyId) + "/join", null, true, ct);
            return root.Get<Party>("party");
        }

        public Task LeaveAsync(CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/parties/leave", null, true, ct);

        public Task InviteAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/parties/invite/" + ApiClient.Seg(userId), null, true, ct);

        public Task KickAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/parties/kick/" + ApiClient.Seg(userId), null, true, ct);

        public Task PromoteAsync(string userId, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/parties/promote/" + ApiClient.Seg(userId), null, true, ct);

        /// <summary>The leader closes the party.</summary>
        public Task CloseAsync(CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/parties/mine", null, true, ct);
    }

    /// <summary>Stored chat history over HTTP. Live chat is on <see cref="RealtimeClient"/>.</summary>
    public sealed class ChatApi
    {
        private readonly ApiClient _api;

        internal ChatApi(ApiClient api)
        {
            _api = api;
        }

        /// <summary>Newest first. Pass the oldest id you have as <paramref name="before"/> for the next page.</summary>
        public async Task<List<ChatMessage>> HistoryAsync(string channel, int limit = 50, long? before = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/chat/history", "channel", channel, "limit", limit, "before", before), ct);
            return root.Get<List<ChatMessage>>("items");
        }
    }
}
