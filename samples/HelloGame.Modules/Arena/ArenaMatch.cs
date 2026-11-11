using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using GameServer.Abstractions;

namespace HelloGame.Modules.Arena;

/// <summary>Registers the "arena" match handler. Define the modes in the "matches" config (see wwwroot/arena/arena-config.json).</summary>
public sealed class ArenaModule : IServerModule
{
    public void Initialize(IModuleInitializer init) =>
        init.RegisterMatchHandler("arena", () => new ArenaMatch());
}

/// <summary>
/// Tank Arena, server-authoritative: clients only send their input (direction, aim, fire); this handler moves the tanks
/// and bullets, decides hits and the winner, and pays the rewards, so nobody can invent a win.
///
/// Messages are JSON in binary frames.
///   client -> server  {"t":"hello","skin":"skin_gold"}   {"t":"in","dx":-1..1,"dy":-1..1,"aim":radians,"fire":true,"seq":n}   {"t":"chat","text":"..."}
///   server -> client  init, players, s (snapshot, 20 per second), hit, left, chat, over, warn
/// </summary>
public sealed class ArenaMatch : MatchHandler
{
    private const double W = 800, H = 500, TankR = 16, BulletR = 4;
    private const double TankSpeed = 150, BulletSpeed = 380, BulletLife = 1.6, FireCooldown = 0.45;
    private const int MaxHp = 3, MaxBulletsPerTank = 5;
    private const double CountdownSeconds = 3, RoundSeconds = 90, WaitForOpponentSeconds = 45, ForfeitSeconds = 15, OverSeconds = 4;

    private static readonly double[][] Walls =
    {
        new double[] { 380, 150, 40, 200 },
        new double[] { 150, 80, 100, 30 },
        new double[] { 550, 80, 100, 30 },
        new double[] { 150, 390, 100, 30 },
        new double[] { 550, 390, 100, 30 },
    };

    private enum Phase { Waiting = 0, Countdown = 1, Playing = 2, Over = 3 }

    private sealed class Tank
    {
        public int Slot;
        public string UserId = "";
        public string Name = "";
        public string Skin = "";
        public double X, Y, Aim;
        public int Hp = MaxHp;
        public int Hits, Shots, Coins;
        public double InX, InY;
        public bool Fire;
        public double Cooldown;
        public long Seq;
        public bool Connected;
        public double Absent;
    }

    private sealed class Bullet
    {
        public double X, Y, Vx, Vy;
        public int Owner;
        public double Life = BulletLife;
    }

    private readonly Dictionary<int, Tank> _tanks = new();
    private readonly List<Bullet> _bullets = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private Phase _phase = Phase.Waiting;
    private double _phaseTime;
    private double _timeLeft = RoundSeconds;
    private bool _finished;

    // ---- lifecycle ------------------------------------------------------------------------------------------

    public override Task OnCreateAsync(IMatch match)
    {
        match.Label = "arena";
        return Task.CompletedTask;
    }

    public override async Task OnJoinAsync(IMatch match, MatchPlayer player)
    {
        if (!_tanks.TryGetValue(player.Slot, out var tank))
        {
            tank = new Tank { Slot = player.Slot, UserId = player.UserId, Name = player.Username };
            Spawn(tank);
            _tanks[player.Slot] = tank;
        }

        tank.Connected = true;
        tank.Absent = 0;

        await match.SendAsync(player, Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "init");
            w.WriteNumber("you", player.Slot);
            w.WriteNumber("w", W);
            w.WriteNumber("h", H);
            w.WriteNumber("r", TankR);
            w.WriteNumber("hp", MaxHp);
            w.WriteStartArray("walls");
            foreach (var wall in Walls)
            {
                w.WriteStartArray();
                foreach (var v in wall) w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));

        await BroadcastPlayersAsync(match);
    }

    public override async Task OnLeaveAsync(IMatch match, MatchPlayer player, MatchLeaveReason reason)
    {
        if (_tanks.TryGetValue(player.Slot, out var tank))
        {
            tank.Connected = false;
            tank.InX = tank.InY = 0;
            tank.Fire = false;
        }

        if (reason != MatchLeaveReason.MatchEnded)
            await match.BroadcastAsync(Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("t", "left");
                w.WriteNumber("slot", player.Slot);
                w.WriteEndObject();
            }));
    }

    public override async Task OnMessageAsync(IMatch match, MatchPlayer player, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0 || data.Length > 512 || !_tanks.TryGetValue(player.Slot, out var tank))
            return;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var type))
                return;

            switch (type.GetString())
            {
                case "in":
                    tank.InX = Clamp(Num(root, "dx"), -1, 1);
                    tank.InY = Clamp(Num(root, "dy"), -1, 1);
                    var aim = Num(root, "aim");
                    if (double.IsFinite(aim)) tank.Aim = aim;
                    tank.Fire = root.TryGetProperty("fire", out var f) && f.ValueKind == JsonValueKind.True;
                    tank.Seq = (long)Num(root, "seq");
                    break;

                case "hello":
                    var skin = root.TryGetProperty("skin", out var s) ? s.GetString() : null;
                    tank.Skin = "";
                    if (!string.IsNullOrEmpty(skin) && skin.StartsWith("skin_", StringComparison.Ordinal) && skin.Length <= 32)
                    {
                        // Only skins the player really owns (bought in the shop) are shown.
                        var state = await match.Services.Economy.GetAsync(player.UserId);
                        if (state.Items.TryGetValue(skin, out var count) && count > 0)
                            tank.Skin = skin;
                    }
                    await BroadcastPlayersAsync(match);
                    break;

                case "chat":
                    var text = root.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (text.Length > 120) text = text[..120];
                        await match.BroadcastAsync(Build(w =>
                        {
                            w.WriteStartObject();
                            w.WriteString("t", "chat");
                            w.WriteNumber("from", player.Slot);
                            w.WriteString("text", text);
                            w.WriteEndObject();
                        }));
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // malformed input is ignored
        }
    }

    public override async Task OnTickAsync(IMatch match)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0.0, 0.1);
        _last = now;

        switch (_phase)
        {
            case Phase.Waiting:
                _phaseTime += dt;
                if (_tanks.Values.Count(t => t.Connected) >= 2)
                {
                    _phase = Phase.Countdown;
                    _phaseTime = CountdownSeconds;
                    foreach (var t in _tanks.Values) Spawn(t);
                }
                else if (_phaseTime > WaitForOpponentSeconds)
                {
                    _phase = Phase.Over;
                    _finished = true;
                    match.End("no_opponent");
                    return;
                }
                break;

            case Phase.Countdown:
                _phaseTime -= dt;
                if (_phaseTime <= 0)
                {
                    _phase = Phase.Playing;
                    _timeLeft = RoundSeconds;
                }
                break;

            case Phase.Playing:
                await StepAsync(match, dt);
                break;

            case Phase.Over:
                _phaseTime += dt;
                if (_phaseTime > OverSeconds)
                    match.End("finished");
                break;
        }

        if (_tanks.Count > 0 && _tanks.Values.Any(t => t.Connected))
            await match.BroadcastAsync(Snapshot(match), MatchDelivery.Sequenced);
    }

    // ---- simulation -----------------------------------------------------------------------------------------

    private async Task StepAsync(IMatch match, double dt)
    {
        _timeLeft -= dt;

        foreach (var t in _tanks.Values)
        {
            if (!t.Connected)
            {
                t.Absent += dt;
                continue;
            }

            Move(t, dt);
            t.Cooldown -= dt;
            if (t.Fire && t.Cooldown <= 0 && _bullets.Count(b => b.Owner == t.Slot) < MaxBulletsPerTank)
            {
                t.Cooldown = FireCooldown;
                t.Shots++;
                _bullets.Add(new Bullet
                {
                    X = t.X + Math.Cos(t.Aim) * (TankR + 6),
                    Y = t.Y + Math.Sin(t.Aim) * (TankR + 6),
                    Vx = Math.Cos(t.Aim) * BulletSpeed,
                    Vy = Math.Sin(t.Aim) * BulletSpeed,
                    Owner = t.Slot
                });
            }
        }

        for (var i = _bullets.Count - 1; i >= 0; i--)
        {
            var b = _bullets[i];
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            b.Life -= dt;

            if (b.Life <= 0 || b.X < 0 || b.X > W || b.Y < 0 || b.Y > H || HitsWall(b.X, b.Y, BulletR))
            {
                _bullets.RemoveAt(i);
                continue;
            }

            foreach (var victim in _tanks.Values)
            {
                if (victim.Slot == b.Owner || victim.Hp <= 0) continue;
                var dx = victim.X - b.X;
                var dy = victim.Y - b.Y;
                if (dx * dx + dy * dy > (TankR + BulletR) * (TankR + BulletR)) continue;

                victim.Hp--;
                if (_tanks.TryGetValue(b.Owner, out var shooter)) shooter.Hits++;
                _bullets.RemoveAt(i);
                var by = b.Owner;
                var slot = victim.Slot;
                var hp = victim.Hp;
                await match.BroadcastAsync(Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("t", "hit");
                    w.WriteNumber("slot", slot);
                    w.WriteNumber("by", by);
                    w.WriteNumber("hp", hp);
                    w.WriteEndObject();
                }));
                break;
            }
        }

        // ---- end of round?
        var alive = _tanks.Values.Where(t => t.Hp > 0).ToList();
        if (_tanks.Count >= 2 && alive.Count <= 1)
        {
            await FinishAsync(match, alive.Count == 1 ? alive[0].Slot : BestOf(), alive.Count == 1 ? "destroyed" : "draw");
            return;
        }

        if (_timeLeft <= 0)
        {
            await FinishAsync(match, BestOf(), "time");
            return;
        }

        foreach (var t in _tanks.Values)
        {
            if (t.Absent > ForfeitSeconds)
            {
                var other = _tanks.Values.FirstOrDefault(o => o.Slot != t.Slot && o.Connected);
                if (other is null)
                {
                    _phase = Phase.Over;
                    _finished = true;
                    match.End("abandoned");
                    return;
                }

                t.Coins = 0;
                await FinishAsync(match, other.Slot, "forfeit");
                return;
            }
        }
    }

    /// <summary>Winner by hit points, then by hits; -1 for a draw.</summary>
    private int BestOf()
    {
        var ordered = _tanks.Values.OrderByDescending(t => t.Hp).ThenByDescending(t => t.Hits).ToList();
        if (ordered.Count < 2) return ordered.Count == 1 ? ordered[0].Slot : -1;
        return ordered[0].Hp == ordered[1].Hp && ordered[0].Hits == ordered[1].Hits ? -1 : ordered[0].Slot;
    }

    private async Task FinishAsync(IMatch match, int winner, string reason)
    {
        if (_finished) return;
        _finished = true;
        _phase = Phase.Over;
        _phaseTime = 0;
        _bullets.Clear();

        foreach (var t in _tanks.Values)
        {
            if (reason == "forfeit" && t.Slot != winner)
                t.Coins = 0;
            else
                t.Coins = winner == -1 ? 15 : t.Slot == winner ? 30 : 10;
        }

        await match.BroadcastAsync(Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "over");
            w.WriteNumber("winner", winner);
            w.WriteString("reason", reason);
            w.WriteStartArray("results");
            foreach (var t in _tanks.Values.OrderBy(x => x.Slot))
            {
                w.WriteStartObject();
                w.WriteNumber("slot", t.Slot);
                w.WriteNumber("hp", t.Hp);
                w.WriteNumber("hits", t.Hits);
                w.WriteNumber("shots", t.Shots);
                w.WriteNumber("coins", t.Coins);
                w.WriteBoolean("won", t.Slot == winner);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));

        var problems = await PayAsync(match, winner);
        if (problems.Count > 0)
            await match.BroadcastAsync(Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("t", "warn");
                w.WriteString("msg", string.Join("; ", problems));
                w.WriteEndObject();
            }));
    }

    /// <summary>Pays coins, records stats and leaderboards. A problem (e.g. a leaderboard missing in the config) never breaks the match.</summary>
    private async Task<List<string>> PayAsync(IMatch match, int winner)
    {
        var problems = new List<string>();
        var s = match.Services;

        foreach (var t in _tanks.Values.OrderBy(x => x.Slot))
        {
            var won = t.Slot == winner;

            async Task Step(string what, Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { problems.Add($"{what}: {ex.Message}"); }
            }

            if (t.Coins > 0)
                await Step("coins", () => s.Economy.ApplyAsync(t.UserId,
                    new EconomyTransaction("arena_match", $"arena:{match.Id}:{t.UserId}").Currency("coins", t.Coins)));

            await Step("stat matches_played", () => s.Progress.AddStatAsync(t.UserId, "matches_played"));
            if (won) await Step("stat matches_won", () => s.Progress.AddStatAsync(t.UserId, "matches_won"));
            if (t.Hits > 0) await Step("stat arena_hits", () => s.Progress.AddStatAsync(t.UserId, "arena_hits", t.Hits));

            if (won)
            {
                await Step("leaderboard arena_wins", () => s.Leaderboards.SubmitAsync("arena_wins", t.UserId, 1));
                await Step("leaderboard arena_weekly", () => s.Leaderboards.SubmitAsync("arena_weekly", t.UserId, 1));
                // Needs the player to have joined the tournament first; otherwise it is simply skipped.
                try { await s.Leaderboards.SubmitTournamentAsync("arena_cup", t.UserId, 1); }
                catch (GameException) { }
                catch (Exception ex) { problems.Add($"tournament: {ex.Message}"); }
            }

            if (t.Hits > 0)
                await Step("leaderboard arena_best_hits", () => s.Leaderboards.SubmitAsync("arena_best_hits", t.UserId, t.Hits));
        }

        return problems;
    }

    private void Spawn(Tank t)
    {
        t.X = t.Slot % 2 == 0 ? 80 : W - 80;
        t.Y = H / 2;
        t.Aim = t.Slot % 2 == 0 ? 0 : Math.PI;
        t.Hp = MaxHp;
        t.InX = t.InY = 0;
        t.Fire = false;
        t.Cooldown = 0;
    }

    private void Move(Tank t, double dt)
    {
        var dx = t.InX;
        var dy = t.InY;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return;
        dx /= len;
        dy /= len;

        var nx = t.X + dx * TankSpeed * dt;
        if (!Blocked(nx, t.Y)) t.X = nx;
        var ny = t.Y + dy * TankSpeed * dt;
        if (!Blocked(t.X, ny)) t.Y = ny;
    }

    private static bool Blocked(double x, double y) =>
        x < TankR || x > W - TankR || y < TankR || y > H - TankR || HitsWall(x, y, TankR);

    private static bool HitsWall(double cx, double cy, double r)
    {
        foreach (var w in Walls)
        {
            var nx = Math.Clamp(cx, w[0], w[0] + w[2]);
            var ny = Math.Clamp(cy, w[1], w[1] + w[3]);
            var dx = cx - nx;
            var dy = cy - ny;
            if (dx * dx + dy * dy < r * r) return true;
        }
        return false;
    }

    // ---- messages -------------------------------------------------------------------------------------------

    private Task BroadcastPlayersAsync(IMatch match) =>
        match.BroadcastAsync(Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "players");
            w.WriteStartArray("list");
            foreach (var t in _tanks.Values.OrderBy(x => x.Slot))
            {
                w.WriteStartObject();
                w.WriteNumber("slot", t.Slot);
                w.WriteString("name", t.Name);
                w.WriteString("skin", t.Skin);
                w.WriteBoolean("online", t.Connected);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));

    private byte[] Snapshot(IMatch match) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "s");
        w.WriteNumber("k", match.Tick);
        w.WriteNumber("ph", (int)_phase);
        w.WriteNumber("tm", Math.Round(_phase == Phase.Countdown ? Math.Max(_phaseTime, 0) : Math.Max(_timeLeft, 0), 1));
        w.WriteStartArray("p");
        foreach (var t in _tanks.Values.OrderBy(x => x.Slot))
        {
            w.WriteStartArray();
            w.WriteNumberValue(t.Slot);
            w.WriteNumberValue(Math.Round(t.X, 1));
            w.WriteNumberValue(Math.Round(t.Y, 1));
            w.WriteNumberValue(Math.Round(t.Aim, 2));
            w.WriteNumberValue(t.Hp);
            w.WriteNumberValue(t.Seq);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("b");
        foreach (var b in _bullets)
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(b.X, 1));
            w.WriteNumberValue(Math.Round(b.Y, 1));
            w.WriteNumberValue(b.Owner);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static byte[] Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static double Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    private static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));
}
