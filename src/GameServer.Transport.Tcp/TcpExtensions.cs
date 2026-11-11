using GameServer.Core.Realtime;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Transports.Tcp;

public static class TcpExtensions
{
    /// <summary>Makes the "tcp" transport available for chat/realtime (4-byte length prefix + UTF-8 JSON, optional TLS).</summary>
    public static RealtimeBuilder AddTcp(this RealtimeBuilder builder) =>
        builder.Add("tcp", sp => ActivatorUtilities.CreateInstance<TcpTransport>(sp));
}
