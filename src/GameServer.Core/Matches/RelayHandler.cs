using GameServer.Abstractions;

namespace GameServer.Core.Matches;

/// <summary>
/// Built-in handler named "relay": forwards every message to the other players, prefixed with one byte, the sender's
/// slot number. Good for turn-based and peer-style games where clients agree on the rules among themselves.
/// </summary>
public sealed class RelayHandler : MatchHandler
{
    public override async Task OnMessageAsync(IMatch match, MatchPlayer player, ReadOnlyMemory<byte> data)
    {
        var framed = new byte[data.Length + 1];
        framed[0] = (byte)player.Slot;
        data.Span.CopyTo(framed.AsSpan(1));
        await match.BroadcastAsync(framed, MatchDelivery.Reliable, player);
    }
}
