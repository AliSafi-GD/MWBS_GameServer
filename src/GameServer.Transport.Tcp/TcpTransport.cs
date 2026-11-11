using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using GameServer.Core.Realtime;

namespace GameServer.Transports.Tcp;

/// <summary>
/// Plain TCP (or TCP + TLS when a certificate is configured) for clients that cannot or should not use WebSockets.
/// Framing: a 4-byte big-endian length, then that many bytes of UTF-8 JSON. The messages are exactly the same as on
/// the WebSocket transport, so the rest of the server does not know the difference.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private readonly RealtimeOptions _options;
    private readonly TcpOptions _tcp;
    private readonly IConnectionHandler _handler;
    private readonly ILogger<TcpTransport> _log;
    private readonly X509Certificate2? _certificate;

    public TcpTransport(IOptions<RealtimeOptions> options, IConfiguration config, IConnectionHandler handler, ILogger<TcpTransport> log)
    {
        _options = options.Value;
        _tcp = config.GetSection("GameServer:Realtime:Tcp").Get<TcpOptions>() ?? new TcpOptions();
        _handler = handler;
        _log = log;

        var tcp = _tcp;
        if (!string.IsNullOrWhiteSpace(tcp.CertificatePath))
        {
            _certificate = X509CertificateLoader.LoadPkcs12FromFile(
                tcp.CertificatePath,
                string.IsNullOrEmpty(tcp.CertificatePassword) ? null : tcp.CertificatePassword);
        }
    }

    public string Name => "tcp";

    public object Describe() => new
    {
        name = Name,
        port = _tcp.Port,
        tls = _certificate is not null,
        framing = "uint32 big-endian length + UTF-8 JSON"
    };

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_tcp.Address), _tcp.Port);
        listener.Start();
        _log.LogInformation("TCP transport listening on {Address}:{Port} (tls: {Tls})",
            _tcp.Address, _tcp.Port, _certificate is not null);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            TcpConnection? connection = null;

            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = _certificate, ClientCertificateRequired = false },
                        handshake.Token);
                    stream = ssl;
                }

                connection = new TcpConnection(stream, client, remote);
                await _handler.OnConnectedAsync(connection);

                var header = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, ct);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length == 0 || length > _options.MaxMessageBytes)
                    {
                        await connection.CloseAsync("invalid_frame_length");
                        break;
                    }

                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, ct);
                    await _handler.OnMessageAsync(connection, Encoding.UTF8.GetString(payload), ct);
                }
            }
            catch (EndOfStreamException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "TCP connection from {Remote} ended with an error.", remote);
            }
            finally
            {
                if (connection is not null)
                    await _handler.OnDisconnectedAsync(connection);
            }
        }
    }

    private sealed class TcpConnection : IConnection
    {
        private readonly Stream _stream;
        private readonly TcpClient _client;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public TcpConnection(Stream stream, TcpClient client, string? remoteAddress)
        {
            _stream = stream;
            _client = client;
            RemoteAddress = remoteAddress;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string TransportName => "tcp";

        public string? RemoteAddress { get; }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            var length = Encoding.UTF8.GetByteCount(message);
            var frame = new byte[4 + length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)length);
            Encoding.UTF8.GetBytes(message, frame.AsSpan(4));

            await _sendLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(frame, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task CloseAsync(string reason)
        {
            try
            {
                _client.Close();
            }
            catch
            {
                // Already closed.
            }

            return Task.CompletedTask;
        }
    }
}
