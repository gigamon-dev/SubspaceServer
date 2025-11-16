using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player's packet has been processed.
    /// </summary>
    /// <param name="player">The player the position packet was for.</param>
    /// <param name="positionPacket">The position packet.</param>
    /// <param name="hasExtraPositionData">Whether the <paramref name="positionPacket"/> contains <see cref="C2S_PositionPacket.Extra"/>.</param>
    [ComponentCallback]
    public delegate void PlayerPositionPacketCallback(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData);
}
