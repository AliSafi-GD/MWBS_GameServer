using GameServer.Client;
#if GAMESERVER_LITENETLIB && !UNITY_WEBGL
using GameServer.Client.Udp;
#endif

namespace Game.Core
{
    /// <summary>
    /// The protocols this build can play a match over. Matches are real time, so they run on UDP (LiteNetLib): a lost
    /// packet is skipped instead of holding back every packet after it, which is what a WebSocket (TCP) does.
    /// The server names the protocol per mode ("transport": "udp" in the "matches" config); WebSocket stays available
    /// for modes that ask for it and for WebGL, where UDP does not exist.
    /// </summary>
    public static class MatchNet
    {
        public static bool HasUdp
        {
            get
            {
#if GAMESERVER_LITENETLIB && !UNITY_WEBGL
                return true;
#else
                return false;
#endif
            }
        }

        public static void Enable(GameClientOptions options)
        {
#if GAMESERVER_LITENETLIB && !UNITY_WEBGL
            options.UseLiteNetLib();
#endif
        }

        /// <summary>Per-tick input: only the newest one matters, so never resend or reorder it.</summary>
        public const MatchDelivery Input = MatchDelivery.Sequenced;
    }
}
