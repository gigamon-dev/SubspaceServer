using SS.Packets.Game;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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

        /// <summary>
        /// Whether the advisor provides the recipients of position packets (<see cref="TryGetPositionRecipients"/>).
        /// </summary>
        /// <remarks>
        /// Only one advisor that provides recipients is meant to be in use in an arena.
        /// This allows such an advisor to detect another one in the same arena.
        /// </remarks>
        bool ProvidesPositionRecipients => false;

        /// <summary>
        /// Gets the players that a player's position packet is to be sent to, instead of considering every player in the arena.
        /// </summary>
        /// <remarks>
        /// This is asked once per position packet, so it should be quick.
        /// The advisor owns the sets it provides (e.g., it can cache them and only update them when something changes).
        /// The caller must not modify them, and can only use them until it returns.
        /// <see cref="EditIndividualPositionPacket"/> is still consulted for each recipient.
        /// </remarks>
        /// <param name="player">The player that the position packet belongs to.</param>
        /// <param name="sendAll">The players that are to be sent the packet regardless of distance.</param>
        /// <param name="sendNormal">The players that are to be sent the packet using the normal rules (based on distance, etc.). A player must not be in both sets.</param>
        /// <returns>
        /// <see langword="true"/> if the advisor provided the recipients, in which case the packet is only sent to players in <paramref name="sendAll"/> and <paramref name="sendNormal"/>.
        /// <see langword="false"/> to use the normal rules for every player in the arena.
        /// </returns>
        bool TryGetPositionRecipients(Player player, [NotNullWhen(true)] out HashSet<Player>? sendAll, [NotNullWhen(true)] out HashSet<Player>? sendNormal)
        {
            sendAll = null;
            sendNormal = null;
            return false;
        }
    }
}
