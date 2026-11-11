using System.Text;

namespace GameServer.Core;

/// <summary>Bound from "GameServer:Admin" (env var GameServer__Admin__Key). Empty key = admin API and panel disabled.</summary>
public sealed class AdminOptions
{
    public string Key { get; set; } = "";

    public void EnsureValid()
    {
        if (!string.IsNullOrEmpty(Key) && Encoding.UTF8.GetByteCount(Key) < 24)
            throw new InvalidOperationException("GameServer:Admin:Key must be at least 24 bytes (or empty to disable the admin API).");
    }
}
