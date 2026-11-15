namespace GameServer.Abstractions;

/// <summary>
/// Entry point of a game module. The host finds every non-abstract type implementing this
/// interface in the module's DLL, creates it with a parameterless constructor, and calls Initialize once.
/// </summary>
public interface IServerModule
{
    void Initialize(IModuleInitializer init);
}

/// <summary>What a module can register with the server. Also hooks and match handlers.</summary>
public interface IModuleInitializer
{
    /// <summary>Registers an RPC callable by clients as POST /v1/rpc/{id}.</summary>
    void RegisterRpc(string id, RpcHandler handler);

    /// <summary>
    /// Runs <paramref name="hook"/> before an operation (names in <see cref="HookOps"/>, RPCs as <c>HookOps.Rpc("id")</c>).
    /// The hook gets the request as JSON and returns it, changed or not; throw <see cref="GameException"/> to reject.
    /// Several hooks on one operation run in registration order.
    /// </summary>
    void RegisterBefore(string operation, BeforeHook hook);

    /// <summary>
    /// Runs <paramref name="hook"/> after an operation succeeded. It cannot change the response, and an
    /// exception in it is logged but never reaches the client.
    /// </summary>
    void RegisterAfter(string operation, AfterHook hook);

    /// <summary>
    /// Registers the game logic of a match mode. A new handler is created for each match of a mode whose
    /// "handler" (default: the mode name) equals <paramref name="name"/>.
    /// </summary>
    void RegisterMatchHandler(string name, Func<MatchHandler> factory);

    /// <summary>
    /// Adds a login method (Apple, email, a store-specific SDK...) to <c>POST /v1/auth/{name}</c>. Like the built-in
    /// ones it is only switched on when <c>GameServer:Auth:Providers</c> lists its name (or, when that setting is
    /// empty, when <see cref="IIdentityProvider.Enabled"/> is true). A duplicate name stops the server at startup.
    /// The default body keeps older hosts and test fakes compiling; the real host overrides it.
    /// </summary>
    void RegisterIdentityProvider(IIdentityProvider provider) =>
        throw new NotSupportedException("This host does not support identity providers from modules.");
}

public delegate Task<string> RpcHandler(RpcContext ctx, string payload);
