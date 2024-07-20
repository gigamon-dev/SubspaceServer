using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PlayerPositionPacketDelegate"/> callback.
    /// </summary>
    public static class PlayerPositionPacketCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's packet has been processed.
        /// </summary>
        /// <param name="player">The player the position packet was for.</param>
        /// <param name="positionPacket">The position packet.</param>
        /// <param name="hasExtraPositionData">Whether the <paramref name="positionPacket"/> contains <see cref="C2S_PositionPacket.Extra"/>.</param>
        public delegate void PlayerPositionPacketDelegate(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData);

        public static void Register(ComponentBroker broker, PlayerPositionPacketDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerPositionPacketDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            broker?.GetCallback<PlayerPositionPacketDelegate>()?.Invoke(player, in positionPacket, in extra, hasExtraPositionData);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, in positionPacket, in extra, hasExtraPositionData);
        }
    }
}
