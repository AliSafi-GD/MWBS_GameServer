using System;
using System.Threading.Tasks;
using Game.Arena;
using GameServer.Client;

namespace Game.Testing
{
    /// <summary>
    /// A headless Arena player for tests: signs in as a fresh guest, finds a match through the matchmaker and plays it
    /// over the match transport using only the server's messages.
    /// </summary>
    public sealed class ArenaBot
    {
        public GameClient Client { get; private set; }
        public ArenaState State { get; } = new ArenaState();
        public MatchConnection Connection { get; private set; }

        private long _seq;

        public static async Task<ArenaBot> CreateAsync(string url, Action<string> log = null)
        {
            var bot = new ArenaBot
            {
                Client = new GameClient(new GameClientOptions
                {
                    BaseUrl = url,
                    Http = new HttpClientHttp(15),
                    Storage = new MemoryStorage(),
                    Log = log
                })
            };
            await bot.Client.Auth.LoginDeviceAsync();
            await bot.Client.Realtime.ConnectAsync();
            return bot;
        }

        /// <summary>Starts listening for the match found event. Call before <see cref="QueueAsync"/>.</summary>
        public Task<MatchConnectInfo> ExpectMatch(int timeoutSeconds = 30)
        {
            var source = new TaskCompletionSource<MatchConnectInfo>();
            Client.Realtime.MatchFound += f => source.TrySetResult(f.ToConnectInfo());
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)).ContinueWith(_ => source.TrySetException(new TimeoutException("no match found in " + timeoutSeconds + " s")));
            return source.Task;
        }

        public Task QueueAsync(string mode = "arena") => Client.Realtime.AddToMatchmakerAsync(mode);

        public async Task ConnectAsync(MatchConnectInfo info)
        {
            Connection = await Client.Matches.ConnectAsync(info);
            Connection.DataReceived += State.Apply;
        }

        /// <summary>
        /// Plays until the round is over. An attacker climbs out of the wall's line of sight, keeps its distance and fires at
        /// the other tank; a bystander stands still.
        /// </summary>
        public async Task PlayAsync(bool attack, int timeoutSeconds = 100)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                double dx = 0, dy = 0, aim = 0;
                var fire = false;
                bool over;
                lock (State.Sync)
                {
                    over = State.IsOver;
                    if (attack && State.HasInit && State.Phase == ArenaState.PhasePlaying &&
                        State.TryGetTank(State.You, out var me) && State.TryGetTank(1 - State.You, out var other))
                    {
                        var sign = me.X < other.X ? 1f : -1f;
                        var desiredX = Math.Min(Math.Max(other.X - sign * 470f, 40f), State.Width - 40f);

                        if (me.Y > 45) dy = -1;
                        else if (Math.Abs(me.X - desiredX) > 12) dx = me.X < desiredX ? 1 : -1;

                        aim = Math.Atan2(other.Y - me.Y, other.X - me.X);
                        fire = me.Y <= 45 && Math.Abs(me.X - desiredX) <= 12;
                    }
                }

                if (over)
                    return;

                if (State.HasInit)
                    await Connection.SendJsonAsync(new { t = "in", dx, dy, aim = Math.Round(aim, 3), fire, seq = ++_seq });

                await Task.Delay(50);
            }

            throw new TimeoutException("round did not end in " + timeoutSeconds + " s");
        }

        public async Task CloseAsync()
        {
            if (Connection != null)
                await Connection.CloseAsync();
            await Client.Realtime.DisconnectAsync();
        }
    }
}
