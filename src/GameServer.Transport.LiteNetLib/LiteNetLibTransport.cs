using System.Collections.Concurrent;
using GameServer.Abstractions;
using GameServer.Core.Matches;
using LiteNetLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Transports.Udp;

/// <summary>Bound from "GameServer:Match:Udp".</summary>
public sealed class LiteNetLibOptions
{
    /// <summary>UDP port the server listens on.</summary>
    public int Port { get; set; } = 7360;

    /// <summary>Host name clients should use for UDP when it differs from the API host (optional, sent with the join token).</summary>
    public string? PublicHost { get; set; }

    public int MaxConnections { get; set; } = 2000;

    /// <summary>A silent client is dropped after this long.</summary>
    public int DisconnectTimeoutMs { get; set; } = 8000;

    /// <summary>How often incoming packets are processed.</summary>
    public int PollIntervalMs { get; set; } = 5;
}

/// <summary>
/// Match transport on UDP using LiteNetLib. Client side: connect with LiteNetLib to host:port and send the join token
/// as a string in the connection request data (<c>NetDataWriter.Put(token)</c>); then every received packet is one
/// match message. The delivery mode (reliable, unreliable, sequenced) is chosen by the match handler per message.
/// </summary>
public sealed class LiteNetLibTransport : IMatchTransport
{
    private readonly LiteNetLibOptions _options;
    private readonly IMatchHost _host;
    private readonly ILogger<LiteNetLibTransport> _log;
    private NetManager? _manager;

    public LiteNetLibTransport(IConfiguration config, IMatchHost host, ILogger<LiteNetLibTransport> log)
    {
        _options = config.GetSection("GameServer:Match:Udp").Get<LiteNetLibOptions>() ?? new LiteNetLibOptions();
        _host = host;
        _log = log;
    }

    public string Name => "udp";

    public object Describe() => new
    {
        name = Name,
        library = "LiteNetLib",
        port = _options.Port,
        host = _options.PublicHost,
        handshake = "Send the join token as a string (NetDataWriter.Put) in the connection request."
    };

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var listener = new EventBasedNetListener();
        var manager = new NetManager(listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = _options.DisconnectTimeoutMs,
        };
        _manager = manager;

        listener.ConnectionRequestEvent += request =>
        {
            try
            {
                if (manager.ConnectedPeersCount >= _options.MaxConnections || request.Data.AvailableBytes == 0)
                {
                    request.Reject();
                    return;
                }

                var token = request.Data.GetString();
                if (!_host.IsTokenValid(token))
                {
                    request.Reject();
                    return;
                }

                var peer = request.Accept();
                peer.Tag = new UdpConnection(peer, token, _host, _log);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Rejecting a UDP connection request.");
                try { request.Reject(); } catch { }
            }
        };

        listener.PeerConnectedEvent += peer =>
        {
            if (peer.Tag is UdpConnection connection)
                connection.Start();
        };

        listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
        {
            if (peer.Tag is UdpConnection connection)
                connection.Receive(reader.GetRemainingBytes());
        };

        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (peer.Tag is UdpConnection connection)
                connection.Stop();
        };

        if (!manager.Start(_options.Port))
            throw new InvalidOperationException($"LiteNetLib could not listen on UDP port {_options.Port}.");

        _log.LogInformation("Match transport 'udp' (LiteNetLib) listening on UDP port {Port}.", _options.Port);

        try
        {
            var delay = Math.Max(_options.PollIntervalMs, 1);
            while (!stoppingToken.IsCancellationRequested)
            {
                manager.PollEvents();
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            manager.Stop();
        }
    }

    /// <summary>One accepted UDP client. Events are handled one after another, in the order they arrived.</summary>
    private sealed class UdpConnection : IMatchConnection
    {
        private readonly NetPeer _peer;
        private readonly string _token;
        private readonly IMatchHost _host;
        private readonly ILogger _log;
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private volatile bool _accepted;
        private volatile bool _refused;

        public UdpConnection(NetPeer peer, string token, IMatchHost host, ILogger log)
        {
            _peer = peer;
            _token = token;
            _host = host;
            _log = log;
            RemoteAddress = peer.Address?.ToString();
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string TransportName => "udp";
        public string? RemoteAddress { get; }

        private void Enqueue(Func<Task> work)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(async _ =>
                {
                    try
                    {
                        await work();
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "UDP connection {Id}: handling an event failed.", Id);
                    }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        public void Start() => Enqueue(async () =>
        {
            if (await _host.OnConnectedAsync(this, _token))
            {
                _accepted = true;
                return;
            }

            _refused = true;
            _peer.Disconnect();
        });

        public void Receive(byte[] data) => Enqueue(async () =>
        {
            if (_accepted && !_refused)
                await _host.OnMessageAsync(this, data);
        });

        public void Stop() => Enqueue(async () =>
        {
            if (_accepted)
            {
                _accepted = false;
                await _host.OnDisconnectedAsync(this);
            }
        });

        public Task SendAsync(ReadOnlyMemory<byte> data, MatchDelivery delivery)
        {
            var method = delivery switch
            {
                MatchDelivery.Unreliable => DeliveryMethod.Unreliable,
                MatchDelivery.Sequenced => DeliveryMethod.Sequenced,
                _ => DeliveryMethod.ReliableOrdered
            };

            var bytes = data.ToArray();
            _peer.Send(bytes, 0, bytes.Length, method);
            return Task.CompletedTask;
        }

        public Task CloseAsync(string reason)
        {
            try
            {
                _peer.Disconnect();
            }
            catch
            {
                // Already gone.
            }

            return Task.CompletedTask;
        }
    }
}

public static class LiteNetLibExtensions
{
    /// <summary>
    /// Makes the UDP transport (LiteNetLib) available under the name "udp" (or <paramref name="name"/>). Switch it on with
    /// <c>GameServer__Match__Transports=websocket,udp</c> and choose it per mode with <c>"transport": "udp"</c>.
    /// </summary>
    public static MatchTransportBuilder AddLiteNetLib(this MatchTransportBuilder builder, string name = "udp") =>
        builder.Add(name, sp => ActivatorUtilities.CreateInstance<LiteNetLibTransport>(sp));
}
