using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameServer.Client;
using UnityEngine;

namespace Game.Core
{
    public enum ChatKind
    {
        Lobby,
        Direct,
        Party,
        Clan
    }

    public sealed class ChatLine
    {
        public long? Id;
        public string SenderId;
        public string Sender;
        public string Text;
        public bool Mine;

        /// <summary>When the server got the message.</summary>
        public DateTime At;

        /// <summary>When this client first saw it live (not set for history). Used to spot our own send racing its echo.</summary>
        internal DateTime Seen;
    }

    /// <summary>One conversation: the lobby room, a friend, the party or the clan.</summary>
    public sealed class ChatThread
    {
        public ChatKind Kind { get; internal set; }

        /// <summary>The channel name the server puts on this conversation's messages.</summary>
        public string Key { get; internal set; }

        /// <summary>The other player, the party or the clan. Null for the lobby.</summary>
        public string TargetId { get; internal set; }

        public string Title { get; internal set; }

        /// <summary>Messages from others that arrived while this conversation was not open.</summary>
        public int Unread { get; internal set; }

        public List<ChatLine> Lines { get; } = new List<ChatLine>();

        public ChatLine Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;

        internal bool Joined;
        internal bool Loaded;
    }

    /// <summary>
    /// All chat: the lobby room, direct messages, party chat and clan chat. The party and clan conversations follow
    /// <see cref="SocialService"/>, so their messages arrive (and count as unread) even while the chat is closed.
    /// Unread counts come from the server (one <c>Chat.ConversationsAsync</c> call), and what was read is saved there,
    /// so every device of the player agrees. Party chat and the lobby are not stored, so they have no read mark.
    /// </summary>
    public sealed class ChatService : IDisposable
    {
        private const int HistorySize = 40;
        private const int MaxLines = 200;

        /// <summary>How long a repeated unstored message from the same sender counts as the same one (our send racing the echo).</summary>
        private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(3);

        private readonly GameClient _client;
        private readonly BackendService _backend;
        private readonly SocialService _social;
        private readonly string _me;
        private readonly Dictionary<string, ChatThread> _threads = new Dictionary<string, ChatThread>();

        /// <summary>The newest message id the player has read, per stored conversation (from the server, then moved by us).</summary>
        private readonly Dictionary<string, long> _readUpTo = new Dictionary<string, long>();
        private bool _synced;

        public event Action<ChatThread, ChatLine> MessageAdded;

        /// <summary>A conversation's lines were replaced (history loaded, a message deleted).</summary>
        public event Action<ChatThread> ThreadChanged;

        public event Action UnreadChanged;

        public ChatThread Lobby { get; }
        public ChatThread PartyThread { get; private set; }
        public ChatThread ClanThread { get; private set; }

        /// <summary>The conversation on screen: its messages are read as they arrive.</summary>
        public ChatThread Active { get; private set; }

        public IEnumerable<ChatThread> DirectThreads => _threads.Values.Where(t => t.Kind == ChatKind.Direct);

        public int TotalUnread => _threads.Values.Sum(t => t.Unread);

        public ChatService(GameClient client, BackendService backend, SocialService social, string myUserId)
        {
            _client = client;
            _backend = backend;
            _social = social;
            _me = myUserId;

            var lobby = Channels.Room(GameIds.LobbyRoom);
            Lobby = Register(new ChatThread { Kind = ChatKind.Lobby, Key = lobby, Title = "Lobby" });

            _client.Realtime.ChatMessageReceived += OnMessage;
            _client.Realtime.ChatMessageDeleted += OnDeleted;
            _backend.Restored += OnRestored;
            _social.Changed += OnSocialChanged;
        }

        public void Dispose()
        {
            _client.Realtime.ChatMessageReceived -= OnMessage;
            _client.Realtime.ChatMessageDeleted -= OnDeleted;
            _backend.Restored -= OnRestored;
            _social.Changed -= OnSocialChanged;
        }

        public bool Has(ChatThread thread) => thread != null && _threads.TryGetValue(thread.Key, out var known) && known == thread;

        /// <summary>The conversation with one player (made on first use).</summary>
        public ChatThread Direct(string userId, string title)
        {
            var key = DirectKey(userId);
            if (_threads.TryGetValue(key, out var thread))
            {
                if (!string.IsNullOrEmpty(title))
                    thread.Title = title;
                return thread;
            }

            return Register(new ChatThread { Kind = ChatKind.Direct, Key = key, TargetId = userId, Title = string.IsNullOrEmpty(title) ? "Player" : title });
        }

        /// <summary>Shows a conversation: joins its channel, loads the stored messages once and marks everything read.</summary>
        public async Task OpenAsync(ChatThread thread)
        {
            Active = thread;
            MarkRead(thread);
            await EnsureAsync(thread);
            if (Active == thread)
                MarkRead(thread);
        }

        public void Close(ChatThread thread)
        {
            if (thread != null && Active == thread)
                Active = null;
        }

        public async Task SendAsync(ChatThread thread, string text)
        {
            await EnsureAsync(thread);
            var sent = await _client.Realtime.SendMessageAsync(thread.Key, text);

            // The server also pushes our message back as an event; Add() keeps only one of the two.
            var line = Add(thread, sent ?? new ChatMessage { SenderId = _me, SenderName = "You", Text = text, Channel = thread.Key }, true);
            if (line == null)
                return;

            // The server marks what we send as read; only the local mark has to follow.
            if (line.Id.HasValue)
                RememberRead(thread, line.Id.Value);
            MarkRead(thread);
            MessageAdded?.Invoke(thread, line);
        }

        // ---- joining and history ------------------------------------------------------------------------------

        private async Task EnsureAsync(ChatThread thread)
        {
            await _backend.EnsureConnectedAsync();

            // Direct messages need no join: the server sends them to both players.
            if (thread.Kind != ChatKind.Direct && !thread.Joined)
            {
                await _client.Realtime.JoinChannelAsync(thread.Key);
                thread.Joined = true;
            }

            // Party chat is never stored. The lobby is only stored when the server is set up to keep rooms.
            if (!thread.Loaded && thread.Kind != ChatKind.Party)
                await LoadHistoryAsync(thread);
        }

        private async Task LoadHistoryAsync(ChatThread thread)
        {
            var history = await _client.Chat.HistoryAsync(thread.Key, HistorySize) ?? new List<ChatMessage>();
            foreach (var m in history)
                Add(thread, m, false);

            var sorted = thread.Lines.OrderBy(l => l.At).ToList();
            thread.Lines.Clear();
            thread.Lines.AddRange(sorted);
            thread.Loaded = true;

            if (thread == Active)
                MarkRead(thread);
            else if (thread.Kind == ChatKind.Direct || thread.Kind == ChatKind.Clan)
                CountUnread(thread);

            ThreadChanged?.Invoke(thread);
        }

        /// <summary>Follows a channel without showing it, so its messages arrive and count as unread.</summary>
        private async void Watch(ChatThread thread)
        {
            try
            {
                await EnsureAsync(thread);
            }
            catch (Exception ex)
            {
                // Opening the conversation tries again and shows the error.
                Debug.LogWarning("[Chat] could not follow " + thread.Key + ": " + ex.Message);
            }
        }

        private async void Forget(ChatThread thread)
        {
            _threads.Remove(thread.Key);
            if (Active == thread)
                Active = null;

            var hadUnread = thread.Unread > 0;
            thread.Unread = 0;
            if (hadUnread)
                UnreadChanged?.Invoke();

            if (!thread.Joined || !_backend.IsOnline)
                return;

            try
            {
                // Also stops the SDK from joining it again after a reconnect.
                await _client.Realtime.LeaveChannelAsync(thread.Key);
            }
            catch (Exception)
            {
                // the server already removed us
            }
        }

        /// <summary>
        /// One call for every stored conversation: who wrote while we were away, how many are unread and the last message
        /// (for the list). Runs once the clan is known, and again after a reconnect.
        /// </summary>
        private async void SyncConversations()
        {
            List<Conversation> list;
            try
            {
                list = await _client.Chat.ConversationsAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chat] could not load the conversations: " + ex.Message);
                return;
            }

            foreach (var c in list)
            {
                ChatThread thread;
                if (c.Kind == "direct" && !string.IsNullOrEmpty(c.PeerId))
                    thread = Direct(c.PeerId, c.Title);
                else if (c.Kind == "group" && ClanThread != null && ClanThread.Key == c.Channel)
                    thread = ClanThread;
                else
                    continue;

                RememberRead(thread, c.LastReadId);
                if (c.LastMessage != null)
                    Add(thread, c.LastMessage, false);

                if (thread != Active)
                    thread.Unread = c.Unread;
            }

            UnreadChanged?.Invoke();
        }

        // ---- following the party and the clan -----------------------------------------------------------------

        private void OnSocialChanged()
        {
            var party = _social.Party;
            if (PartyThread != null && (party == null || party.Id != PartyThread.TargetId))
            {
                Forget(PartyThread);
                PartyThread = null;
            }

            if (party != null && PartyThread == null)
            {
                PartyThread = Register(new ChatThread { Kind = ChatKind.Party, Key = Channels.Party(party.Id), TargetId = party.Id, Title = "Party" });
                Watch(PartyThread);
            }

            var clan = _social.Clan?.Group;
            if (ClanThread != null && (clan == null || clan.Id != ClanThread.TargetId))
            {
                Forget(ClanThread);
                ClanThread = null;
            }

            if (clan != null && ClanThread == null)
            {
                ClanThread = Register(new ChatThread { Kind = ChatKind.Clan, Key = Channels.Group(clan.Id), TargetId = clan.Id, Title = clan.Name });
                Watch(ClanThread);
            }

            var friends = _social.Friends.Where(f => f.State == FriendStates.Friend && f.Profile != null).ToList();
            foreach (var f in friends)
                Direct(f.Profile.Id, NameOf(f.Profile));

            if (!_synced)
            {
                _synced = true;
                SyncConversations();
            }
        }

        private void OnRestored()
        {
            // Messages sent while we were offline are only in the stored history.
            foreach (var thread in _threads.Values)
                thread.Loaded = false;

            if (Active != null)
                Watch(Active);

            SyncConversations();
        }

        // ---- incoming -----------------------------------------------------------------------------------------

        private void OnMessage(ChatMessage message)
        {
            if (message?.Channel == null)
                return;

            if (!_threads.TryGetValue(message.Channel, out var thread))
            {
                // A player we have no conversation with yet wrote to us.
                var other = OtherOf(message.Channel);
                if (other == null)
                    return;

                thread = Direct(other, message.SenderId == other ? message.SenderName : null);
            }

            var line = Add(thread, message, true);
            if (line == null)
                return;

            if (thread == Active)
            {
                MarkRead(thread);
            }
            else if (!line.Mine && thread.Kind != ChatKind.Lobby)
            {
                thread.Unread++;
                UnreadChanged?.Invoke();
            }

            MessageAdded?.Invoke(thread, line);
        }

        private void OnDeleted(string channel, long id)
        {
            if (channel == null || !_threads.TryGetValue(channel, out var thread))
                return;

            if (thread.Lines.RemoveAll(l => l.Id == id) > 0)
                ThreadChanged?.Invoke(thread);
        }

        private ChatLine Add(ChatThread thread, ChatMessage m, bool live)
        {
            var now = DateTime.UtcNow;
            if (m.Id.HasValue)
            {
                // Stored messages carry an id: the same id is the same message.
                if (thread.Lines.Any(l => l.Id == m.Id))
                    return null;
            }
            else if (thread.Lines.Any(l => l.Id == null && l.SenderId == m.SenderId && l.Text == m.Text && now - l.Seen < EchoWindow))
            {
                // Not stored (no id): the same sender saying the same thing a moment ago is our send racing the echo.
                return null;
            }

            var line = new ChatLine
            {
                Id = m.Id,
                SenderId = m.SenderId,
                Sender = m.SenderName ?? "System",
                Text = m.Text,
                Mine = m.SenderId == _me,
                At = m.CreatedUtc == default(DateTime) ? now : m.CreatedUtc,
                Seen = live ? now : DateTime.MinValue
            };

            thread.Lines.Add(line);
            if (thread.Lines.Count > MaxLines)
                thread.Lines.RemoveAt(0);
            return line;
        }

        // ---- read state (kept on the server) -------------------------------------------------------------------

        private static bool IsStored(ChatThread thread) => thread.Kind == ChatKind.Direct || thread.Kind == ChatKind.Clan;

        private void MarkRead(ChatThread thread)
        {
            var newest = thread.Lines.LastOrDefault(l => l.Id.HasValue);
            if (newest != null && IsStored(thread) && RememberRead(thread, newest.Id.Value))
                SaveRead(thread, newest.Id.Value);

            if (thread.Unread == 0)
                return;

            thread.Unread = 0;
            UnreadChanged?.Invoke();
        }

        /// <summary>After loading the history: what came after the read mark is unread.</summary>
        private void CountUnread(ChatThread thread)
        {
            var read = _readUpTo.TryGetValue(thread.Key, out var id) ? id : 0;
            var others = thread.Lines.Where(l => !l.Mine && l.Id.HasValue).ToList();
            var counted = others.Count(l => l.Id.Value > read);

            // Every loaded message is unread: there may be more before them, so keep the server's (bigger) count.
            var unread = counted == others.Count && thread.Unread > counted ? thread.Unread : counted;
            if (unread == thread.Unread)
                return;

            thread.Unread = unread;
            UnreadChanged?.Invoke();
        }

        /// <summary>Moves the local read mark forward. True when it moved.</summary>
        private bool RememberRead(ChatThread thread, long id)
        {
            if (_readUpTo.TryGetValue(thread.Key, out var known) && known >= id)
                return false;
            _readUpTo[thread.Key] = id;
            return true;
        }

        private async void SaveRead(ChatThread thread, long id)
        {
            try
            {
                await _client.Chat.MarkReadAsync(thread.Key, id);
            }
            catch (Exception ex)
            {
                // Not fatal: the next read saves a newer mark.
                Debug.LogWarning("[Chat] could not save what was read in " + thread.Title + ": " + ex.Message);
            }
        }

        // ---- names --------------------------------------------------------------------------------------------

        private ChatThread Register(ChatThread thread)
        {
            _threads[thread.Key] = thread;
            return thread;
        }

        /// <summary>The server names a direct channel "dm:&lt;a&gt;:&lt;b&gt;" with the two ids in ordinal order.</summary>
        private string DirectKey(string other) =>
            string.CompareOrdinal(_me, other) < 0 ? "dm:" + _me + ":" + other : "dm:" + other + ":" + _me;

        private string OtherOf(string channel)
        {
            if (!channel.StartsWith("dm:", StringComparison.Ordinal))
                return null;

            var parts = channel.Substring(3).Split(':');
            if (parts.Length != 2)
                return null;

            return parts[0] == _me ? parts[1] : parts[1] == _me ? parts[0] : null;
        }

        private static string NameOf(PublicProfile p) =>
            !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : (string.IsNullOrEmpty(p.Username) ? "Player" : p.Username);
    }
}
