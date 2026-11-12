using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameServer.Client;

namespace Game.Core
{
    /// <summary>Connection health: signed-out sessions and the realtime link going away and coming back.</summary>
    public sealed class BackendService : IDisposable
    {
        private readonly GameClient _client;
        private Task _connecting;

        public event Action<string> Lost;
        public event Action Restored;
        public event Action SessionEnded;

        public bool IsOnline => _client.Realtime.IsConnected;

        public BackendService(GameClient client)
        {
            _client = client;
            _client.Realtime.Disconnected += OnLost;
            _client.Realtime.Reconnected += OnRestored;
            _client.Auth.SessionEnded += OnSessionEnded;
        }

        public void Dispose()
        {
            _client.Realtime.Disconnected -= OnLost;
            _client.Realtime.Reconnected -= OnRestored;
            _client.Auth.SessionEnded -= OnSessionEnded;
        }

        private void OnLost(string reason) => Lost?.Invoke(reason);
        private void OnRestored() => Restored?.Invoke();
        private void OnSessionEnded() => SessionEnded?.Invoke();

        /// <summary>Opens the realtime link once; callers that arrive meanwhile wait for the same attempt.</summary>
        public Task EnsureConnectedAsync()
        {
            if (_client.Realtime.IsConnected)
                return Task.CompletedTask;

            return _connecting ?? (_connecting = ConnectAsync());
        }

        private async Task ConnectAsync()
        {
            try
            {
                await _client.Realtime.ConnectAsync();
            }
            finally
            {
                _connecting = null;
            }
        }
    }

    public sealed class SocialService : IDisposable
    {
        private readonly GameClient _client;

        public event Action Changed;

        public List<Friend> Friends { get; private set; } = new List<Friend>();
        public Party Party { get; private set; }
        public PartyInvite PendingInvite { get; private set; }

        /// <summary>The clan I am a member of, with my role in it ("owner", "admin" or "member"), or null.</summary>
        public MyGroup Clan { get; private set; }
        public List<GroupMember> ClanMembers { get; private set; } = new List<GroupMember>();

        /// <summary>Clans that invited me.</summary>
        public List<Group> ClanInvites { get; private set; } = new List<Group>();

        public bool OwnsClan => Clan != null && Clan.Role == "owner";

        /// <summary>Friend requests waiting for an answer plus party and clan invites: shown as a badge.</summary>
        public int PendingCount => Friends.Count(f => f.State == FriendStates.InviteReceived) + (PendingInvite != null ? 1 : 0) + ClanInvites.Count;

        public SocialService(GameClient client)
        {
            _client = client;
            _client.Realtime.PartyInvited += OnInvited;
            _client.Realtime.PartyUpdated += OnPartyUpdated;
            _client.Realtime.PartyLeft += OnPartyLeft;
            _client.Realtime.EventReceived += OnEvent;
        }

        public void Dispose()
        {
            _client.Realtime.PartyInvited -= OnInvited;
            _client.Realtime.PartyUpdated -= OnPartyUpdated;
            _client.Realtime.PartyLeft -= OnPartyLeft;
            _client.Realtime.EventReceived -= OnEvent;
        }

        private void OnInvited(PartyInvite invite)
        {
            PendingInvite = invite;
            Changed?.Invoke();
        }

        private void OnPartyUpdated(Party party)
        {
            Party = party;
            if (PendingInvite != null && party != null && PendingInvite.PartyId == party.Id)
                PendingInvite = null;
            Changed?.Invoke();
        }

        private void OnPartyLeft(PartyLeft left)
        {
            Party = null;
            Changed?.Invoke();
        }

        private async void OnEvent(RealtimeMessage message)
        {
            var friends = message.Type != null && message.Type.StartsWith("friend.");
            var groups = message.Type != null && message.Type.StartsWith("group.");
            if (!friends && !groups)
                return;

            try
            {
                if (friends)
                    await RefreshFriendsAsync();
                else
                    await RefreshClanAsync();
            }
            catch (Exception)
            {
                // the next manual refresh will show the error
            }
        }

        public async Task RefreshAsync()
        {
            var friends = _client.Social.Friends.ListAsync();
            var party = _client.Social.Parties.MineAsync();
            var clan = LoadClanAsync();
            await Task.WhenAll(friends, party, clan);
            Friends = friends.Result ?? new List<Friend>();
            Party = party.Result;
            Changed?.Invoke();
        }

        // ---- clan ---------------------------------------------------------------------------------------------

        public async Task RefreshClanAsync()
        {
            await LoadClanAsync();
            Changed?.Invoke();
        }

        private async Task LoadClanAsync()
        {
            var mine = await _client.Social.Groups.MineAsync() ?? new List<MyGroup>();
            var clans = mine.Where(g => g.Group != null && g.Group.Type == GameIds.ClanType).ToList();
            var clan = clans.FirstOrDefault(g => g.Role == "owner" || g.Role == "admin" || g.Role == "member");
            var members = clan == null
                ? new List<GroupMember>()
                : await _client.Social.Groups.MembersAsync(clan.Group.Id) ?? new List<GroupMember>();

            Clan = clan;
            ClanMembers = members;
            ClanInvites = clans.Where(g => g.Role == "invited").Select(g => g.Group).ToList();
        }

        public async Task CreateClanAsync(string name)
        {
            await _client.Social.Groups.CreateAsync(new NewGroup { Name = name, Type = GameIds.ClanType, Open = true });
            await RefreshClanAsync();
        }

        public async Task<List<Group>> SearchClansAsync(string query) =>
            await _client.Social.Groups.SearchAsync(string.IsNullOrWhiteSpace(query) ? null : query.Trim(), GameIds.ClanType) ?? new List<Group>();

        /// <summary>Joins an open clan or accepts an invite; a closed clan gets a request. Returns the resulting role.</summary>
        public async Task<string> JoinClanAsync(string clanId)
        {
            var role = await _client.Social.Groups.JoinAsync(clanId);
            await RefreshClanAsync();
            return role;
        }

        /// <summary>Leaves the clan. The owner can only leave when nobody else is left; otherwise disband it.</summary>
        public async Task LeaveClanAsync()
        {
            if (Clan == null)
                return;

            await _client.Social.Groups.LeaveAsync(Clan.Group.Id);
            await RefreshClanAsync();
        }

        /// <summary>The owner deletes the clan for everyone. This cannot be undone.</summary>
        public async Task DisbandClanAsync()
        {
            if (Clan == null)
                return;

            await _client.Social.Groups.DeleteAsync(Clan.Group.Id);
            await RefreshClanAsync();
        }

        // ---- friends and party --------------------------------------------------------------------------------

        public async Task RefreshFriendsAsync()
        {
            Friends = await _client.Social.Friends.ListAsync() ?? new List<Friend>();
            Changed?.Invoke();
        }

        public async Task<string> AddFriendAsync(string username)
        {
            var result = await _client.Social.Friends.AddAsync(null, username);
            await RefreshFriendsAsync();
            return result.State;
        }

        public async Task AcceptFriendAsync(string userId)
        {
            await _client.Social.Friends.AddAsync(userId);
            await RefreshFriendsAsync();
        }

        public async Task RemoveFriendAsync(string userId)
        {
            await _client.Social.Friends.RemoveAsync(userId);
            await RefreshFriendsAsync();
        }

        public async Task CreatePartyAsync()
        {
            Party = await _client.Social.Parties.CreateAsync();
            Changed?.Invoke();
        }

        public async Task LeavePartyAsync()
        {
            await _client.Social.Parties.LeaveAsync();
            Party = null;
            Changed?.Invoke();
        }

        public async Task JoinInvitedPartyAsync()
        {
            if (PendingInvite == null)
                return;

            Party = await _client.Social.Parties.JoinAsync(PendingInvite.PartyId);
            PendingInvite = null;
            Changed?.Invoke();
        }

        public Task InviteToPartyAsync(string userId) => _client.Social.Parties.InviteAsync(userId);

        public async Task KickFromPartyAsync(string userId)
        {
            await _client.Social.Parties.KickAsync(userId);
            Party = await _client.Social.Parties.MineAsync();
            Changed?.Invoke();
        }
    }

    public sealed class RankService
    {
        private readonly GameClient _client;

        public RankService(GameClient client)
        {
            _client = client;
        }

        public Task<Board> GetAsync(string boardId, int limit = 20) => _client.Competitions.LeaderboardAsync(boardId, limit, 1);
    }
}
