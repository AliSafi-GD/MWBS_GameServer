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
    /// Runs callbacks on the thread that created the client (the Unity main thread in a game), so event handlers can
    /// touch GameObjects. Without a synchronization context (a console app) callbacks run on the calling thread.
    /// </summary>
    internal sealed class Dispatcher
    {
        private readonly SynchronizationContext _context = SynchronizationContext.Current;
        private readonly Action<string> _log;

        public Dispatcher(Action<string> log)
        {
            _log = log;
        }

        public void Post(Action action)
        {
            if (action == null)
                return;

            if (_context == null)
            {
                Run(action);
                return;
            }

            _context.Post(_ => Run(action), null);
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
