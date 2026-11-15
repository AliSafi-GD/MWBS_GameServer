using System.Net.WebSockets;

namespace GameServer.Transports.Ws;

/// <summary>
/// Closing a server WebSocket without losing the last frames. Aborting right after sending the close frame can reset
/// the TCP connection while frames sent just before (an "unauthorized" error, say) are still on their way, and the
/// client then never reads them. So the server sends its close frame, waits for the client's answer, and aborts only
/// when the client does not answer in time.
/// </summary>
internal static class WebSocketClosing
{
    /// <summary>How long a client gets to answer our close frame before the connection is cut.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sends the close frame (under the send lock, so it never overlaps a message being sent). A receive loop that is
    /// waiting then gets the client's answer and ends; if the client never answers, the socket is aborted after
    /// <see cref="Grace"/> so that loop cannot hang.
    /// </summary>
    public static async Task CloseOutputAsync(WebSocket socket, SemaphoreSlim sendLock, string reason)
    {
        var sent = false;
        try
        {
            using var timeout = new CancellationTokenSource(Grace);
            await sendLock.WaitAsync(timeout.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
                    sent = true;
                }
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch
        {
            // Already closed or broken: cut it below.
        }

        if (!sent)
        {
            if (socket.State is not (WebSocketState.Closed or WebSocketState.CloseSent))
                Abort(socket);
            return;
        }

        _ = AbortIfStillOpenAsync(socket);
    }

    /// <summary>
    /// Call when the receive loop has ended: reads (and drops) whatever the client still sends until its close frame
    /// arrives, at most <see cref="Grace"/>, so the connection ends with a normal close handshake. Aborts otherwise.
    /// </summary>
    public static async Task FinishAsync(WebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Grace);
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.CloseSent)
                await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        }
        catch
        {
            // Timed out or broken.
        }
        finally
        {
            if (socket.State != WebSocketState.Closed)
                Abort(socket);
        }
    }

    private static async Task AbortIfStillOpenAsync(WebSocket socket)
    {
        await Task.Delay(Grace);
        if (socket.State != WebSocketState.Closed)
            Abort(socket);
    }

    private static void Abort(WebSocket socket)
    {
        try { socket.Abort(); } catch { /* disposed */ }
    }
}
