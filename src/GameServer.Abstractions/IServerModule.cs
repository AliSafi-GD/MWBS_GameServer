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
}

public delegate Task<string> RpcHandler(RpcContext ctx, string payload);
