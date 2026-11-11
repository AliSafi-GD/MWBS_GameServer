using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameServer.Client;

namespace Game.Core
{
    /// <summary>Matchmaker queue, private matches and the hand-off of the chosen match to the Arena scene.</summary>
    public sealed class PlayService : IDisposable
    {
        private readonly GameClient _client;
        private readonly BackendService _backend;

        public event Action SearchChanged;
        public event Action<string> SearchEnded;
        public event Action<MatchConnectInfo> MatchReady;

        public List<MatchMode> Modes { get; private set; } = new List<MatchMode>();
        public Ticket Ticket { get; private set; }
        public DateTime SearchStartedUtc { get; private set; }

        /// <summary>How to reach the match the Arena scene should join. Set right before the scene loads.</summary>
        public MatchConnectInfo Pending { get; set; }

        public bool IsSearching => Ticket != null;
        public bool HasRanked => Modes.Any(m => m.Name == GameIds.RankedMode);
        public bool HasFriendly => Modes.Any(m => m.Name == GameIds.FriendlyMode && m.AllowClientCreate);

        public PlayService(GameClient client, BackendService backend)
        {
            _client = client;
            _backend = backend;
            _client.Realtime.MatchFound += OnFound;
            _client.Realtime.MatchmakerExpired += OnExpired;
        }

        public void Dispose()
        {
            _client.Realtime.MatchFound -= OnFound;
            _client.Realtime.MatchmakerExpired -= OnExpired;
        }

        public async Task LoadModesAsync()
        {
            var modes = await _client.Matches.ModesAsync();
            Modes = modes?.Modes ?? new List<MatchMode>();
        }

        public async Task FindMatchAsync()
        {
            await _backend.EnsureConnectedAsync();
            Ticket = await _client.Realtime.AddToMatchmakerAsync(GameIds.RankedMode);
            SearchStartedUtc = DateTime.UtcNow;
            SearchChanged?.Invoke();
        }

        public async Task CancelSearchAsync()
        {
            var ticket = Ticket;
            if (ticket == null)
                return;

            Ticket = null;
            SearchChanged?.Invoke();
            await _client.Realtime.RemoveFromMatchmakerAsync(ticket.Id);
        }

        public async Task CreatePrivateAsync(string label)
        {
            var created = await _client.Matches.CreateAsync(GameIds.FriendlyMode, label);
            Begin(created.Connect);
        }

        public async Task<List<MatchInfo>> ListPrivateAsync() =>
            await _client.Matches.ListAsync(GameIds.FriendlyMode, 20) ?? new List<MatchInfo>();

        public async Task JoinPrivateAsync(string matchId) => Begin(await _client.Matches.JoinAsync(matchId));

        /// <summary>Opens the match connection with the protocol the mode uses.</summary>
        public Task<MatchConnection> ConnectAsync(MatchConnectInfo info) => _client.Matches.ConnectAsync(info);

        /// <summary>Asks for a new seat (and token) in a match that is already running, after a dropped connection.</summary>
        public Task<MatchConnectInfo> RejoinAsync(string matchId) => _client.Matches.JoinAsync(matchId);

        private void OnFound(MatchFound found)
        {
            Ticket = null;
            SearchChanged?.Invoke();
            Begin(found.ToConnectInfo());
        }

        private void OnExpired(TicketExpired expired)
        {
            if (Ticket == null || Ticket.Id != expired.TicketId)
                return;

            Ticket = null;
            SearchChanged?.Invoke();
            SearchEnded?.Invoke(expired.Reason);
        }

        private void Begin(MatchConnectInfo info)
        {
            Pending = info;
            MatchReady?.Invoke(info);
        }
    }
}
