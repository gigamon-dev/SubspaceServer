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
        public delegate void PlayerPositionPacketDelegate(Player player, in C2S_PositionPacket positionPacket);

        public static void Register(ComponentBroker broker, PlayerPositionPacketDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerPositionPacketDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, in C2S_PositionPacket positionPacket)
        {
            broker?.GetCallback<PlayerPositionPacketDelegate>()?.Invoke(player, in positionPacket);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, in positionPacket);
        }
    }
}
