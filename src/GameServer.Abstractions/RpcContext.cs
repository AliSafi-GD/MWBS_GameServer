namespace GameServer.Abstractions;

/// <summary>Everything an RPC handler is allowed to touch. Grows as Core gets new services.</summary>
public sealed class RpcContext
{
    public RpcContext(
        string userId, IStorage storage, IConfig config, IEconomy economy,
        ILeaderboards leaderboards, INotifications notifications, ISocial social, IChat chat, IMatches matches,
        IProgress progress, CancellationToken cancellationToken)
    {
        UserId = userId;
        Storage = storage;
        Config = config;
        Economy = economy;
        Leaderboards = leaderboards;
        Notifications = notifications;
        Social = social;
        Chat = chat;
        Matches = matches;
        Progress = progress;
        CancellationToken = cancellationToken;
    }

    public string UserId { get; }
    public IStorage Storage { get; }
    public IConfig Config { get; }
    public IEconomy Economy { get; }
    public ILeaderboards Leaderboards { get; }
    public INotifications Notifications { get; }
    public ISocial Social { get; }
    public IChat Chat { get; }
    public IMatches Matches { get; }
    public IProgress Progress { get; }
    public CancellationToken CancellationToken { get; }
}
