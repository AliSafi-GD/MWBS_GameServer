namespace GameServer.Abstractions;

/// <summary>
/// Runs before an operation. <paramref name="requestJson"/> is the request as camelCase JSON; return it
/// (changed or not) and the operation continues with what you return. Throw <see cref="GameException"/> to reject.
/// </summary>
public delegate Task<string> BeforeHook(HookContext ctx, string requestJson);

/// <summary>
/// Runs after an operation succeeded. <paramref name="requestJson"/> is what the operation actually used and
/// <paramref name="responseJson"/> what the client receives. Errors are logged and ignored.
/// </summary>
public delegate Task AfterHook(HookContext ctx, string requestJson, string responseJson);

/// <summary>What a hook is allowed to touch. <see cref="UserId"/> is the player who triggered the operation.</summary>
public sealed class HookContext
{
    public HookContext(
        string operation, string userId, IStorage storage, IConfig config, IEconomy economy,
        ILeaderboards leaderboards, INotifications notifications, ISocial social, IChat chat,
        CancellationToken cancellationToken)
    {
        Operation = operation;
        UserId = userId;
        Storage = storage;
        Config = config;
        Economy = economy;
        Leaderboards = leaderboards;
        Notifications = notifications;
        Social = social;
        Chat = chat;
        CancellationToken = cancellationToken;
    }

    public string Operation { get; }
    public string UserId { get; }
    public IStorage Storage { get; }
    public IConfig Config { get; }
    public IEconomy Economy { get; }
    public ILeaderboards Leaderboards { get; }
    public INotifications Notifications { get; }
    public ISocial Social { get; }
    public IChat Chat { get; }
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Operation names for <see cref="IModuleInitializer.RegisterBefore"/> / <see cref="IModuleInitializer.RegisterAfter"/>.
/// Request JSON shapes are listed in the README ("Hooks").
/// </summary>
public static class HookOps
{
    /// <summary>AFTER only. Request: <c>{ "provider": "device|googleplay|phone", "isNew": bool }</c>; response: the login result.
    /// <c>isNew</c> is a best guess (the account was created within the last few seconds), so use an idempotency key for gifts.</summary>
    public const string AuthLogin = "auth.login";

    public const string AccountUpdate = "account.update";
    public const string AccountDelete = "account.delete";

    public const string FriendsAdd = "friends.add";
    public const string FriendsRemove = "friends.remove";
    public const string FriendsBlock = "friends.block";
    public const string FriendsUnblock = "friends.unblock";

    public const string GroupsCreate = "groups.create";
    public const string GroupsUpdate = "groups.update";
    public const string GroupsDelete = "groups.delete";
    public const string GroupsJoin = "groups.join";
    public const string GroupsLeave = "groups.leave";
    public const string GroupsInvite = "groups.invite";
    public const string GroupsAccept = "groups.accept";
    public const string GroupsKick = "groups.kick";
    public const string GroupsBan = "groups.ban";
    public const string GroupsUnban = "groups.unban";
    public const string GroupsPromote = "groups.promote";
    public const string GroupsDemote = "groups.demote";
    public const string GroupsTransfer = "groups.transfer";

    public const string PartiesCreate = "parties.create";
    public const string PartiesJoin = "parties.join";
    public const string PartiesLeave = "parties.leave";
    public const string PartiesInvite = "parties.invite";
    public const string PartiesKick = "parties.kick";
    public const string PartiesPromote = "parties.promote";
    public const string PartiesClose = "parties.close";

    public const string MatchmakerAdd = "matchmaker.add";
    public const string MatchesCreate = "matches.create";
    public const string MatchesJoin = "matches.join";

    public const string ShopBuy = "shop.buy";
    public const string DailyClaim = "daily.claim";
    public const string AchievementsClaim = "achievements.claim";
    public const string QuestsClaim = "quests.claim";
    public const string ChatJoin = "chat.join";
    public const string ChatSend = "chat.send";

    /// <summary>Hooks on a module RPC. Request and response are the raw strings of the call.</summary>
    public static string Rpc(string id) => "rpc:" + id;
}
