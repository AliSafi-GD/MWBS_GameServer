#if GAMESERVER_LITENETLIB
using System;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;

namespace GameServer.Client.Udp
{
    /// <summary>
    /// Match connection over UDP with LiteNetLib. Sends the join token as a string in the connection request (what the
    /// server's "udp" transport expects). Every received packet is one match message; the delivery mode of each
    /// send (reliable, unreliable, sequenced) is yours to choose.
    /// </summary>
    public sealed class LiteNetLibMatchTransportClient : IMatchTransportClient
    {
        public string Name => "udp";

        public Task<IMatchTransportConnection> ConnectAsync(MatchConnectInfo info, Uri apiUri, CancellationToken ct)
        {
            var endpoint = info.Endpoint;
            var host = endpoint == null || endpoint["host"] == null ? null : (string)endpoint["host"];
            if (string.IsNullOrEmpty(host))
                host = apiUri.Host;
            var port = endpoint == null || endpoint["port"] == null ? 7360 : (int)endpoint["port"];

            var completion = new TaskCompletionSource<IMatchTransportConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = new EventBasedNetListener();
            // Events come straight from LiteNetLib's own thread, so nothing needs polling (and the Unity main thread is free).
            var manager = new NetManager(listener) { UnsyncedEvents = true, DisconnectTimeout = 10000 };
            var connection = new Connection(manager);

            listener.PeerConnectedEvent += peer =>
            {
                connection.Peer = peer;
                completion.TrySetResult(connection);
            };

            listener.PeerDisconnectedEvent += (peer, disconnect) =>
            {
                if (!completion.Task.IsCompleted)
                    completion.TrySetException(new ApiException("network_error", "UDP connection failed: " + disconnect.Reason, 0));
                connection.RaiseClosed(disconnect.Reason.ToString());
            };

            listener.NetworkErrorEvent += (point, error) =>
            {
                if (!completion.Task.IsCompleted)
                    completion.TrySetException(new ApiException("network_error", "UDP error: " + error, 0));
            };

            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                var data = reader.GetRemainingBytes();
                reader.Recycle();
                connection.OnData(data);
            };

            if (!manager.Start())
                throw new ApiException("network_error", "Could not open a UDP socket.", 0);

            var writer = new NetDataWriter();
            writer.Put(info.Token);
            manager.Connect(host, port, writer);

            ct.Register(() => completion.TrySetCanceled());
            Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                if (completion.TrySetException(new ApiException("network_error", "UDP connection to " + host + ":" + port + " timed out.", 0)))
                    manager.Stop();
            });

            return completion.Task;
        }

        private sealed class Connection : IMatchTransportConnection
        {
            private readonly NetManager _manager;
            private int _closed;

            public NetPeer Peer;

            public Connection(NetManager manager)
            {
                _manager = manager;
            }

            public event Action<byte[]> DataReceived;
            public event Action<string> Closed;

            public void Start()
            {
                // LiteNetLib already runs; nothing to start.
            }

            public void OnData(byte[] data)
            {
                try { DataReceived?.Invoke(data); } catch (Exception) { }
            }

            public Task SendAsync(byte[] data, MatchDelivery delivery, CancellationToken ct)
            {
                var peer = Peer;
                if (peer == null || peer.ConnectionState != ConnectionState.Connected)
                    throw new ApiException("not_connected", "The UDP connection is not open.", 0);

                DeliveryMethod method;
                switch (delivery)
                {
                    case MatchDelivery.Unreliable: method = DeliveryMethod.Unreliable; break;
                    case MatchDelivery.Sequenced: method = DeliveryMethod.Sequenced; break;
                    default: method = DeliveryMethod.ReliableOrdered; break;
                }

                peer.Send(data, method);
                return Task.FromResult(0);
            }

            public Task CloseAsync()
            {
                RaiseClosed("closed_by_client");
                return Task.FromResult(0);
            }

            public void RaiseClosed(string reason)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                    return;

                try { _manager.Stop(true); } catch (Exception) { }
                try { Closed?.Invoke(reason); } catch (Exception) { }
            }
        }
    }

    public static class LiteNetLibClientOptionsExtensions
    {
        /// <summary>Lets the client connect to matches whose mode uses the "udp" transport.</summary>
        public static GameClientOptions UseLiteNetLib(this GameClientOptions options)
        {
            options.MatchTransports.RemoveAll(t => t.Name == "udp");
            options.MatchTransports.Add(new LiteNetLibMatchTransportClient());
            return options;
        }
    }
}
#endif
