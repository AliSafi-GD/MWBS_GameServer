using System.Threading.Channels;
using GameServer.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameServer.Core.Matches;

/// <summary>
/// One running match. All calls into the <see cref="MatchHandler"/> go through a single queue that one task works
/// through, so the handler never runs in parallel with itself, whatever protocol the players use.
/// </summary>
internal sealed class MatchInstance : IMatch
{
    private sealed class Slot
    {
        public required MatchPlayer Player { get; init; }
        public IMatchConnection? Connection { get; set; }
        public bool Kicked { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly MatchHandler _handler;
    private readonly ILogger _log;
    private Timer? _timer;
    private int _tickPending;
    private long _tick;
    private int _ended;
    private DateTime? _emptySinceUtc;
    private bool _everConnected;
    private volatile string? _label;

    public MatchInstance(
        string id, MatchMode mode, MatchHandler handler, string? label, string? parameters, bool joinable, MatchServices services, ILogger log)
    {
        Services = services;
        Id = id;
        Definition = mode;
        _handler = handler;
        _label = label;
        Parameters = parameters;
        Joinable = joinable;
        _log = log;
    }

    public string Id { get; }
    public MatchMode Definition { get; }
    public string Mode => Definition.Name;
    public string? Parameters { get; }
    public bool Joinable { get; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public string? EndReason { get; private set; }
    public object? State { get; set; }
    public MatchServices Services { get; }

    /// <summary>Raised once, after the handler's OnEndAsync.</summary>
    public event Action<MatchInstance>? Ended;

    public string? Label
    {
        get => _label;
        set => _label = value is null ? null : (value.Length > 100 ? value[..100] : value);
    }

    public long Tick => Interlocked.Read(ref _tick);

    public bool IsEnded => Volatile.Read(ref _ended) == 1;

    public IReadOnlyList<MatchPlayer> Players
    {
        get { lock (_gate) return _slots.Select(s => s.Player).ToArray(); }
    }

    public IReadOnlyList<MatchPlayer> Connected
    {
        get { lock (_gate) return _slots.Where(s => s.Connection is not null).Select(s => s.Player).ToArray(); }
    }

    public MatchInfo Info()
    {
        lock (_gate)
        {
            var connected = _slots.Count(s => s.Connection is not null);
            var open = Joinable && !IsEnded && _slots.Count < Definition.MaxPlayers;
            return new MatchInfo(Id, Mode, Label, _slots.Count, connected, Definition.MaxPlayers, open, CreatedUtc);
        }
    }

    // ---- roster ---------------------------------------------------------------------------------------------

    /// <summary>The player's place in the match; adds them if there is room. Null when the match is full.</summary>
    public MatchPlayer? AddPlayer(string userId, string username)
    {
        lock (_gate)
        {
            var existing = _slots.FirstOrDefault(s => s.Player.UserId == userId);
            if (existing is not null)
                return existing.Player;
            if (_slots.Count >= Definition.MaxPlayers)
                return null;

            var slot = new Slot { Player = new MatchPlayer(userId, username, _slots.Count) };
            _slots.Add(slot);
            return slot.Player;
        }
    }

    public MatchPlayer? FindPlayer(string userId)
    {
        lock (_gate)
            return _slots.FirstOrDefault(s => s.Player.UserId == userId)?.Player;
    }

    public bool IsKicked(string userId)
    {
        lock (_gate)
            return _slots.Any(s => s.Player.UserId == userId && s.Kicked);
    }

    // ---- lifetime -------------------------------------------------------------------------------------------

    public void Start()
    {
        _ = Task.Run(RunAsync);

        Enqueue(async () =>
        {
            try
            {
                await _handler.OnCreateAsync(this);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Match {Id} ({Mode}): OnCreate failed, ending the match.", Id, Mode);
                End("handler_error");
            }
        });

        if (Definition.TickRate > 0)
        {
            var period = TimeSpan.FromMilliseconds(1000.0 / Definition.TickRate);
            _timer = new Timer(_ => TickTimer(), null, period, period);
        }
    }

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Match {Id} ({Mode}): handler threw an exception.", Id, Mode);
            }
        }
    }

    private bool Enqueue(Func<Task> work) => _queue.Writer.TryWrite(work);

    private void TickTimer()
    {
        if (IsEnded || Interlocked.CompareExchange(ref _tickPending, 1, 0) != 0)
            return; // the previous tick is still running: skip this one instead of piling up

        var queued = Enqueue(async () =>
        {
            try
            {
                if (IsEnded) return;
                Interlocked.Increment(ref _tick);
                await _handler.OnTickAsync(this);
            }
            finally
            {
                Volatile.Write(ref _tickPending, 0);
            }
        });

        if (!queued)
            Volatile.Write(ref _tickPending, 0);
    }

    public void End(string? reason = null)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1)
            return;

        EndReason = reason ?? "ended";
        _timer?.Dispose();

        Enqueue(async () =>
        {
            (MatchPlayer Player, IMatchConnection Connection)[] connected;
            lock (_gate)
            {
                connected = _slots.Where(s => s.Connection is not null).Select(s => (s.Player, s.Connection!)).ToArray();
                foreach (var slot in _slots)
                    slot.Connection = null; // transports' disconnect callbacks now find nothing to report
            }

            foreach (var (player, _) in connected)
                await Safe(() => _handler.OnLeaveAsync(this, player, MatchLeaveReason.MatchEnded));

            await Safe(() => _handler.OnEndAsync(this));

            foreach (var (_, connection) in connected)
                await Safe(() => connection.CloseAsync(EndReason ?? "ended"));

            _queue.Writer.TryComplete();
            Ended?.Invoke(this);
        });
    }

    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Match {Id} ({Mode}): handler threw an exception while ending.", Id, Mode);
        }
    }

    /// <summary>Ends the match when nobody ever came or everybody left long enough ago. Called about once a second.</summary>
    public void CheckTimeouts(DateTime nowUtc)
    {
        if (IsEnded)
            return;

        bool endForJoin, endForEmpty;
        lock (_gate)
        {
            endForJoin = !_everConnected && (nowUtc - CreatedUtc).TotalSeconds >= Definition.JoinTimeoutSeconds;
            endForEmpty = _everConnected && _emptySinceUtc is { } since
                          && (nowUtc - since).TotalSeconds >= Definition.EmptyTimeoutSeconds;
        }

        if (endForJoin) End("join_timeout");
        else if (endForEmpty) End("empty");
    }

    // ---- connections ----------------------------------------------------------------------------------------

    /// <summary>Attaches a connection to the player after the handler accepted them. False = refused.</summary>
    public Task<bool> ConnectAsync(IMatchConnection connection, MatchPlayer player)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = Enqueue(async () =>
        {
            try
            {
                done.TrySetResult(await DoConnectAsync(connection, player));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Match {Id} ({Mode}): joining failed.", Id, Mode);
                done.TrySetResult(false);
            }
        });

        if (!queued)
            done.TrySetResult(false);
        return done.Task;
    }

    private async Task<bool> DoConnectAsync(IMatchConnection connection, MatchPlayer player)
    {
        if (IsEnded || IsKicked(player.UserId))
            return false;

        if (!await _handler.OnJoinAttemptAsync(this, player))
            return false;

        IMatchConnection? previous;
        lock (_gate)
        {
            var slot = _slots.First(s => s.Player.UserId == player.UserId);
            previous = slot.Connection;
            slot.Connection = connection;
            _everConnected = true;
            _emptySinceUtc = null;
        }

        if (previous is not null && !ReferenceEquals(previous, connection))
            await Safe(() => previous.CloseAsync("replaced_by_new_connection"));

        await _handler.OnJoinAsync(this, player);
        return true;
    }

    public void Disconnected(IMatchConnection connection)
    {
        Enqueue(async () =>
        {
            Slot? slot;
            lock (_gate)
            {
                slot = _slots.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));
                if (slot is null)
                    return; // already replaced by a newer connection, or the match ended

                slot.Connection = null;
                if (_slots.All(s => s.Connection is null))
                    _emptySinceUtc = DateTime.UtcNow;
            }

            await _handler.OnLeaveAsync(this, slot.Player, slot.Kicked ? MatchLeaveReason.Kicked : MatchLeaveReason.Disconnected);
        });
    }

    public void Message(IMatchConnection connection, ReadOnlyMemory<byte> data)
    {
        var copy = data.ToArray(); // the transport may reuse its buffer as soon as we return

        Enqueue(async () =>
        {
            MatchPlayer? player;
            lock (_gate)
                player = _slots.FirstOrDefault(s => ReferenceEquals(s.Connection, connection))?.Player;

            if (player is not null)
                await _handler.OnMessageAsync(this, player, copy);
        });
    }

    // ---- IMatch ---------------------------------------------------------------------------------------------

    public async Task SendAsync(MatchPlayer player, ReadOnlyMemory<byte> data, MatchDelivery delivery = MatchDelivery.Reliable)
    {
        IMatchConnection? connection;
        lock (_gate)
            connection = _slots.FirstOrDefault(s => s.Player.UserId == player.UserId)?.Connection;

        if (connection is not null)
            await SendSafeAsync(connection, data, delivery);
    }

    public async Task BroadcastAsync(ReadOnlyMemory<byte> data, MatchDelivery delivery = MatchDelivery.Reliable, MatchPlayer? except = null)
    {
        IMatchConnection[] targets;
        lock (_gate)
        {
            targets = _slots
                .Where(s => s.Connection is not null && (except is null || s.Player.UserId != except.UserId))
                .Select(s => s.Connection!)
                .ToArray();
        }

        await Task.WhenAll(targets.Select(c => SendSafeAsync(c, data, delivery)));
    }

    private async Task SendSafeAsync(IMatchConnection connection, ReadOnlyMemory<byte> data, MatchDelivery delivery)
    {
        try
        {
            await connection.SendAsync(data, delivery);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Match {Id}: sending to a connection failed.", Id);
        }
    }

    public async Task KickAsync(MatchPlayer player, string? reason = null)
    {
        IMatchConnection? connection;
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => s.Player.UserId == player.UserId);
            if (slot is null)
                return;
            slot.Kicked = true;
            connection = slot.Connection;
        }

        if (connection is not null)
            await Safe(() => connection.CloseAsync(reason ?? "kicked"));
    }
}
