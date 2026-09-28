using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="ballPacket">The packet.</param>
    [ComponentCallback]
    public delegate void BallPacketSentCallback(Arena arena, ref readonly BallPacket ballPacket);
}
