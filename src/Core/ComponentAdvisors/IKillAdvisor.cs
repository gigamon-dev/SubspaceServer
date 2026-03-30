using System.Collections.Generic;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an adivsor on kill activities.
    /// </summary>
    public interface IKillAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Modifies the amount of points a player will receive for making a kill.
        /// </summary>
        /// <param name="arena">The arena the kill took place in.</param>
        /// <param name="killer">The player who made the kill.</param>
        /// <param name="killed">The player who got killed.</param>
        /// <param name="bounty">The number displayed in the kill message.</param>
        /// <param name="flags">The number of flags the killed player was carrying.</param>
        /// <returns>The number of points to be added to the total.</returns>
        short KillPoints(Arena arena, Player killer, Player killed, int bounty, int flags) => 0;

        /// <summary>
        /// Modifies a player's death packet before it is sent out.
        /// </summary>
        /// <param name="arena">The arena the kill took place in.</param>
        /// <param name="killer">The player who made the kill. May be set to <see langword="null"/> to drop the death packet. Make sure the killer is in the same arena.</param>
        /// <param name="killed">The player who got killed.</param>
        /// <param name="bounty">The number displayed in the kill message.</param>
        void EditDeath(Arena arena, ref Player killer, ref Player killed, ref short bounty) { }

        /// <summary>
        /// Filters which players in the arena receive the S2C Kill notification packet.
        /// </summary>
        /// <param name="arena">The arena the kill took place in.</param>
        /// <param name="killer">The player who made the kill.</param>
        /// <param name="killed">The player who got killed.</param>
        /// <param name="recipients">
        /// The set of players who will receive the kill notification. Remove players from this set to suppress the packet for them.
        /// </param>
        void FilterKillNotification(Arena arena, Player killer, Player killed, HashSet<Player> recipients) { }
    }
}
