using System;
using System.Collections.Generic;
using System.Threading;

namespace GameServer.Client
{
    /// <summary>
    /// An error answered by the server (<c>{"error":"code","message":"..."}</c>) or a network problem
    /// (<see cref="Code"/> is "network_error" and <see cref="Status"/> is 0).
    /// </summary>
    public sealed class ApiException : Exception
    {
        public string Code { get; }
        public int Status { get; }

        public ApiException(string code, string message, int status = 0) : base(message)
        {
            Code = code;
            Status = status;
        }

        public bool IsNetworkError => Code == "network_error";

        public override string ToString() => "ApiException(" + Code + (Status > 0 ? ", " + Status : "") + "): " + Message;
    }

    /// <summary>Small key-value store for the session and the device id. In Unity the default is PlayerPrefs.</summary>
    public interface IClientStorage
    {
        string Get(string key);
        void Set(string key, string value);
        void Delete(string key);
    }

    public sealed class MemoryStorage : IClientStorage
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public string Get(string key)
        {
            lock (_values)
                return _values.TryGetValue(key, out var v) ? v : null;
        }

        public void Set(string key, string value)
        {
            lock (_values)
                _values[key] = value;
        }

        public void Delete(string key)
        {
            lock (_values)
                _values.Remove(key);
        }
    }

    /// <summary>
    /// Where the SDK raises its events (realtime messages, match data, connection changes). Set
    /// <see cref="GameClientOptions.Dispatcher"/> to choose; the default is <see cref="SynchronizationContextDispatcher"/>.
    /// </summary>
    public interface IDispatcher
    {
        void Post(Action action);
    }

    /// <summary>
    /// The default: runs callbacks on the thread that created it (the Unity main thread in a game, because the client is
    /// created there), so event handlers can touch GameObjects. Without a synchronization context (a console app)
    /// callbacks run on the calling thread.
    /// </summary>
    public sealed class SynchronizationContextDispatcher : IDispatcher
    {
        private readonly SynchronizationContext _context;

        /// <param name="context">Null = the context of the current thread.</param>
        public SynchronizationContextDispatcher(SynchronizationContext context = null)
        {
            _context = context ?? SynchronizationContext.Current;
        }

        public void Post(Action action)
        {
            if (_context == null)
                action();
            else
                _context.Post(_ => action(), null);
        }
    }

    /// <summary>Runs every callback at once, on the network thread. For tests, bots and servers without a main loop.</summary>
    public sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>Wraps the chosen dispatcher so an exception in a game's handler is logged instead of breaking the SDK.</summary>
    internal sealed class Dispatcher
    {
        private readonly IDispatcher _inner;
        private readonly Action<string> _log;

        public Dispatcher(IDispatcher inner, Action<string> log)
        {
            _inner = inner;
            _log = log;
        }

        public void Post(Action action)
        {
            if (action == null)
                return;

            _inner.Post(() => Run(action));
        }

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log?.Invoke("A handler threw: " + ex);
            }
        }
    }
}
