namespace GameServer.Transports.Tcp;

/// <summary>Bound from "GameServer:Realtime:Tcp".</summary>
public sealed class TcpOptions
{
    public string Address { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 7351;

    /// <summary>Path to a .pfx file. When set, the TCP transport speaks TLS; leave empty for plain TCP.</summary>
    public string CertificatePath { get; set; } = "";

    public string CertificatePassword { get; set; } = "";
}
