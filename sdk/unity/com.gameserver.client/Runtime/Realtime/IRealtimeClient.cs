using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>
    /// The live connection: chat, matchmaker and pushed events (friend requests, party invites, notifications...).
    /// All events are raised on the thread that created the <see cref="GameClient"/> (the Unity main thread).
    /// </summary>
    public interface IRealtimeClient
    {
        bool IsConnected { get; }

        /// <summary>The protocol in use, for example "websocket".</summary>
        string TransportName { get; }

        event Action Connected;

        /// <summary>The connection ended; the argument is the reason. With AutoReconnect the client tries again by itself.</summary>
        event Action<string> Disconnected;

        /// <summary>Connected again after a drop (channels were joined again).</summary>
        event Action Reconnected;

        event Action<ChatMessage> ChatMessageReceived;

        /// <summary>(channel, message id)</summary>
        event Action<string, long> ChatMessageDeleted;

        event Action<MatchFound> MatchFound;

        event Action<TicketExpired> MatchmakerExpired;

        event Action<NotificationEvent> NotificationReceived;

        event Action<PartyInvite> PartyInvited;

        event Action<Party> PartyUpdated;

        event Action<PartyLeft> PartyLeft;

        /// <summary>Every pushed event, including ones without a typed event here (friend.request, group.invite, goal.completed, game.*).</summary>
        event Action<RealtimeMessage> EventReceived;

        /// <summary>Listens to one event type, e.g. <c>On("friend.request", data =&gt; ...)</c>. Handlers run on the main thread.</summary>
        void On(string type, Action<JToken> handler);

        void Off(string type, Action<JToken> handler);

        /// <summary>
        /// Opens the connection with the configured protocol and signs in with the player's token.
        /// Log in first. Pass <paramref name="transport"/> to override <see cref="GameClientOptions.RealtimeTransport"/>.
        /// </summary>
        Task ConnectAsync(string transport = null, CancellationToken ct = default(CancellationToken));

        /// <summary>Closes the connection and stops reconnecting.</summary>
        Task DisconnectAsync();

        /// <summary>
        /// Sends a message and waits for the server's reply. A reply of type "error" is thrown as <see cref="ApiException"/>.
        /// Most callers use the typed methods below; this is for message types you add on the server.
        /// </summary>
        Task<RealtimeMessage> RequestAsync(string type, object data = null, CancellationToken ct = default(CancellationToken));

        /// <summary>Joins a channel to receive its messages (see <see cref="Channels"/>). Rejoined automatically after a reconnect.</summary>
        Task<string> JoinChannelAsync(string channel, CancellationToken ct = default(CancellationToken));

        Task LeaveChannelAsync(string channel, CancellationToken ct = default(CancellationToken));

        /// <summary>Sends a message. Rooms and groups need a prior <see cref="JoinChannelAsync"/>; direct messages do not.</summary>
        Task<ChatMessage> SendMessageAsync(string channel, string text, CancellationToken ct = default(CancellationToken));

        Task<List<ChatMessage>> ChannelHistoryAsync(string channel, int limit = 50, long? before = null, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Joins the matchmaking queue of a mode. When enough players are found you get <see cref="MatchFound"/>.
        /// <paramref name="properties"/> are the rule values the mode asks for (for example region or skill).
        /// Pass <paramref name="partyId"/> to queue your whole party.
        /// </summary>
        Task<Ticket> AddToMatchmakerAsync(string mode, Dictionary<string, object> properties = null, string partyId = null, CancellationToken ct = default(CancellationToken));

        Task<bool> RemoveFromMatchmakerAsync(string ticketId, CancellationToken ct = default(CancellationToken));

        /// <summary>Creates a match yourself (only for modes that allow it) and returns how to connect.</summary>
        Task<MatchCreated> CreateMatchAsync(string mode, string label = null, string parameters = null, CancellationToken ct = default(CancellationToken));

        Task<MatchConnectInfo> JoinMatchAsync(string matchId, CancellationToken ct = default(CancellationToken));

        Task<List<MatchInfo>> ListMatchesAsync(string mode = null, int? limit = null, CancellationToken ct = default(CancellationToken));
    }
}
