using SS.Packets.Game;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on player position activities.
    /// </summary>
    public interface IPlayerPositionAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Modifies a player's position packet before it is sent out.
        /// </summary>
        /// <remarks>
        /// The adviser may edit the packet, but should not rely on this function for notification, as other advisers may be consulted. 
        /// Instead the <see cref="ComponentCallbacks.PlayerPositionPacketCallback"/> should be used for notification.
        /// </remarks>
        /// <param name="player">The player that the position packet belongs to.</param>
        /// <param name="positionPacket">The position packet.</param>
        void EditPositionPacket(Player player, ref C2S_PositionPacket positionPacket) { }

        /// <summary>
        /// Modifies a player's position packet before it is sent to a specific player.
        /// </summary>
        /// <param name="player">The player that the position packet belongs to.</param>
        /// <param name="toPlayer">The player that the position packet will be sent to.</param>
        /// <param name="positionPacket">The position packet.</param>
        /// <param name="extra">The extra position data.</param>
        /// <param name="extraLength">The length of the <paramref name="extra"/> position data (0 = none, 2 = energy only, 10 = all extra position data).</param>
        /// <returns><see langword="true"/> if this function modified the packet. Otherwise, <see langword="false"/>.</returns>
        bool EditIndividualPositionPacket(Player player, Player toPlayer, ref C2S_PositionPacket positionPacket, ref ExtraPositionData extra, ref int extraLength) => false;
    }
}
