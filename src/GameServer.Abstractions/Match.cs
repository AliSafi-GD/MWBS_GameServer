namespace GameServer.Abstractions;

/// <summary>How a message to a match client is delivered. Transports that cannot do the difference (WebSocket) deliver everything reliably.</summary>
public enum MatchDelivery
{
    /// <summary>Always arrives, in order.</summary>
    Reliable,

    /// <summary>May be lost or arrive out of order (fast state updates). UDP-like transports honour this.</summary>
    Unreliable,

    /// <summary>Not guaranteed, but older packets than the newest received are dropped.</summary>
    Sequenced
}

public enum MatchLeaveReason
{
    /// <summary>The client closed the connection on purpose.</summary>
    Left,

    /// <summary>The connection broke or timed out. The player may reconnect with a new token.</summary>
    Disconnected,

    /// <summary>Removed with <see cref="IMatch.KickAsync"/>.</summary>
    Kicked,

    /// <summary>The match ended.</summary>
    MatchEnded
}

/// <summary>The services a <see cref="MatchHandler"/> can use (the same ones an RPC gets).</summary>
public sealed class MatchServices
{
    public MatchServices(IEconomy economy, IProgress progress, ILeaderboards leaderboards, INotifications notifications, IConfig config)
    {
        Economy = economy;
        Progress = progress;
        Leaderboards = leaderboards;
        Notifications = notifications;
        Config = config;
    }

    public IEconomy Economy { get; }
    public IProgress Progress { get; }
    public ILeaderboards Leaderboards { get; }
    public INotifications Notifications { get; }
    public IConfig Config { get; }
}

/// <param name="Slot">0, 1, 2... in the order players were added to the match; stays the same when the player reconnects.</param>
public sealed record MatchPlayer(string UserId, string Username, int Slot);

/// <summary>
/// One running match, as seen by its <see cref="MatchHandler"/>. All handler methods of a match run one at a time
/// (never in parallel), so a handler can keep its data in <see cref="State"/> without locks.
/// </summary>
public interface IMatch
{
    string Id { get; }

    /// <summary>The mode name from the "matches" config.</summary>
    string Mode { get; }

    /// <summary>Free text players can see in the match list, e.g. a map name. Change it any time.</summary>
    string? Label { get; set; }

    /// <summary>The JSON given when the match was created (empty for matchmaker matches).</summary>
    string? Parameters { get; }

    /// <summary>Number of ticks run so far (0 when the mode has no tick rate).</summary>
    long Tick { get; }

    /// <summary>Everybody who belongs to the match, connected or not.</summary>
    IReadOnlyList<MatchPlayer> Players { get; }

    /// <summary>The players that have a live connection right now.</summary>
    IReadOnlyList<MatchPlayer> Connected { get; }

    /// <summary>Your own data for this match.</summary>
    object? State { get; set; }

    /// <summary>Server services a handler may need, e.g. to pay rewards or record stats when the match is decided.</summary>
    MatchServices Services { get; }

    Task SendAsync(MatchPlayer player, ReadOnlyMemory<byte> data, MatchDelivery delivery = MatchDelivery.Reliable);

    Task BroadcastAsync(ReadOnlyMemory<byte> data, MatchDelivery delivery = MatchDelivery.Reliable, MatchPlayer? except = null);

    /// <summary>Disconnects a player and does not let them back in with their old token.</summary>
    Task KickAsync(MatchPlayer player, string? reason = null);

    /// <summary>Ends the match after the current handler call returns: disconnects everybody and calls <see cref="MatchHandler.OnEndAsync"/>.</summary>
    void End(string? reason = null);
}

/// <summary>
/// The game logic of a match mode. Register a factory with <c>init.RegisterMatchHandler("name", () =&gt; new MyHandler())</c>;
/// a new instance is created for every match. The data inside messages is whatever your client and handler agree on.
/// </summary>
public abstract class MatchHandler
{
    /// <summary>Called once, before anybody joins.</summary>
    public virtual Task OnCreateAsync(IMatch match) => Task.CompletedTask;

    /// <summary>Return false to refuse a player (the connection is closed).</summary>
    public virtual Task<bool> OnJoinAttemptAsync(IMatch match, MatchPlayer player) => Task.FromResult(true);

    /// <summary>A player's connection is established (also after a reconnect).</summary>
    public virtual Task OnJoinAsync(IMatch match, MatchPlayer player) => Task.CompletedTask;

    public virtual Task OnLeaveAsync(IMatch match, MatchPlayer player, MatchLeaveReason reason) => Task.CompletedTask;

    /// <summary>A message arrived from a client.</summary>
    public virtual Task OnMessageAsync(IMatch match, MatchPlayer player, ReadOnlyMemory<byte> data) => Task.CompletedTask;

    /// <summary>Called <c>tickRate</c> times per second when the mode has a tick rate.</summary>
    public virtual Task OnTickAsync(IMatch match) => Task.CompletedTask;

    /// <summary>The match is over (ended by the handler, an admin, a timeout or server shutdown).</summary>
    public virtual Task OnEndAsync(IMatch match) => Task.CompletedTask;
}

public sealed record MatchInfo(
    string Id, string Mode, string? Label, int Players, int Connected, int MaxPlayers, bool Open, DateTime CreatedUtc);

/// <summary>What a client needs to connect to a match over the chosen protocol.</summary>
/// <param name="Transport">"websocket", "udp", ... (the match transport that carries this mode).</param>
/// <param name="Token">One-time token; send it when connecting (the transport says how).</param>
/// <param name="Endpoint">Transport-specific address info, e.g. a path or a port.</param>
public sealed record MatchConnectInfo(
    string MatchId, string Mode, string Transport, string Token, DateTime ExpiresUtc, object? Endpoint);

/// <summary>Match control from server code (RPCs and hooks).</summary>
public interface IMatches
{
    /// <summary>Creates a match of a mode defined in the "matches" config. Players join with <see cref="JoinAsync"/>.</summary>
    Task<MatchInfo> CreateAsync(string mode, string? label = null, string? parametersJson = null, CancellationToken ct = default);

    /// <summary>Reserves a place for the player and returns what their client needs to connect.</summary>
    Task<MatchConnectInfo> JoinAsync(string matchId, string userId, CancellationToken ct = default);

    MatchInfo? Get(string matchId);

    IReadOnlyList<MatchInfo> List(string? mode = null, int limit = 50);

    Task EndAsync(string matchId, string? reason = null, CancellationToken ct = default);
}
