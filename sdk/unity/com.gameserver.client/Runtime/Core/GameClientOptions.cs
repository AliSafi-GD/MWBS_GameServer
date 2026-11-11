using System;
using System.Collections.Generic;

namespace GameServer.Client
{
    public sealed class GameClientOptions
    {
        /// <summary>Address of the server, e.g. "http://localhost:5000" or "https://game.example.com".</summary>
        public string BaseUrl { get; set; } = "http://localhost:5000";

        /// <summary>HTTP implementation. Null = UnityWebRequest inside Unity, HttpClient elsewhere.</summary>
        public IHttp Http { get; set; }

        /// <summary>Where the session and device id are kept. Null = PlayerPrefs inside Unity, memory elsewhere.</summary>
        public IClientStorage Storage { get; set; }

        /// <summary>Write SDK diagnostics here, for example <c>Debug.Log</c>. Null = silent.</summary>
        public Action<string> Log { get; set; }

        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>Realtime protocol to use: "websocket" or "tcp" (or the name of a transport you added). Must be enabled on the server.</summary>
        public string RealtimeTransport { get; set; } = "websocket";

        /// <summary>Reconnect and sign in again by itself when the realtime connection drops.</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>How often the client pings the server (the server drops silent connections after about two minutes).</summary>
        public int PingIntervalSeconds { get; set; } = 25;

        public int RealtimeRequestTimeoutSeconds { get; set; } = 15;

        /// <summary>Realtime transports the client knows. Add your own with <see cref="IRealtimeTransport"/>.</summary>
        public List<IRealtimeTransport> RealtimeTransports { get; } = new List<IRealtimeTransport>
        {
            new WebSocketRealtimeTransport(),
            new TcpRealtimeTransport()
        };

        /// <summary>Match transports the client knows. Add LiteNetLib with <c>UseLiteNetLib()</c> from the optional assembly.</summary>
        public List<IMatchTransportClient> MatchTransports { get; } = new List<IMatchTransportClient>
        {
            new WebSocketMatchTransportClient()
        };
    }
}
