using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BallPacketSentCallback
    {
        /// <summary>
        /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="ballPacket">The packet.</param>
        public delegate void BallPacketSentDelegate(Arena arena, ref readonly BallPacket ballPacket);
    }
}
