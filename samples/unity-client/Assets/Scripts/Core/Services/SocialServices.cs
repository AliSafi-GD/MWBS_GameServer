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

        /// <summary>Friend requests waiting for an answer plus a party invite: shown as a badge.</summary>
        public int PendingCount => Friends.Count(f => f.State == FriendStates.InviteReceived) + (PendingInvite != null ? 1 : 0);

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
            if (message.Type == null || !message.Type.StartsWith("friend."))
                return;

            try
            {
                await RefreshFriendsAsync();
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
            await Task.WhenAll(friends, party);
            Friends = friends.Result ?? new List<Friend>();
            Party = party.Result;
            Changed?.Invoke();
        }

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

    public sealed class ChatLine
    {
        public string SenderId;
        public string Sender;
        public string Text;
        public bool Mine;
    }

    /// <summary>The lobby chat room: history once, then live messages. Own messages are shown once even if the server echoes them.</summary>
    public sealed class ChatService : IDisposable
    {
        private readonly GameClient _client;
        private readonly BackendService _backend;
        private readonly string _me;
        private readonly string _channel = Channels.Room(GameIds.LobbyRoom);
        private readonly List<(string Text, DateTime At)> _recentOwn = new List<(string, DateTime)>();
        private bool _joined;

        public event Action<ChatLine> MessageAdded;

        public List<ChatLine> Lines { get; } = new List<ChatLine>();

        public ChatService(GameClient client, BackendService backend, string myUserId)
        {
            _client = client;
            _backend = backend;
            _me = myUserId;
            _client.Realtime.ChatMessageReceived += OnMessage;
        }

        public void Dispose() => _client.Realtime.ChatMessageReceived -= OnMessage;

        /// <summary>Joins the room and loads the stored history the first time; later calls do nothing.</summary>
        public async Task JoinAsync()
        {
            if (_joined)
                return;

            await _backend.EnsureConnectedAsync();
            await _client.Realtime.JoinChannelAsync(_channel);
            _joined = true;

            var history = await _client.Chat.HistoryAsync(_channel, 40) ?? new List<ChatMessage>();
            foreach (var m in history.OrderBy(m => m.CreatedUtc))
                Add(m);
        }

        public async Task SendAsync(string text)
        {
            await JoinAsync();
            var at = DateTime.UtcNow;
            _recentOwn.Add((text, at));
            var sent = await _client.Realtime.SendMessageAsync(_channel, text);

            // If the server echoed the message while we waited, the echo already added the line and removed the mark.
            if (_recentOwn.RemoveAll(o => o.Text == text && o.At == at) > 0)
                Add(sent ?? new ChatMessage { SenderId = _me, SenderName = "You", Text = text });
        }

        private void OnMessage(ChatMessage message)
        {
            if (message.Channel != _channel)
                return;

            if (message.SenderId == _me)
            {
                var index = _recentOwn.FindIndex(o => o.Text == message.Text && (DateTime.UtcNow - o.At).TotalSeconds < 10);
                if (index >= 0)
                {
                    _recentOwn.RemoveAt(index);
                    Add(message);
                    return;
                }
            }

            Add(message);
        }

        private void Add(ChatMessage m)
        {
            var line = new ChatLine { SenderId = m.SenderId, Sender = m.SenderName ?? "System", Text = m.Text, Mine = m.SenderId == _me };
            Lines.Add(line);
            if (Lines.Count > 200)
                Lines.RemoveAt(0);
            MessageAdded?.Invoke(line);
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
