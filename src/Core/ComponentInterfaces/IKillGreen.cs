using SS.Packets.Game;

namespace SS.Core.ComponentInterfaces
{
    public interface IKillGreen : IComponentInterface
    {
        /// <summary>
        /// Method that returns the geen to be used in the kill packet.
        /// That is, when a player is killed, what green (prize) should be dropped.
        /// </summary>
        /// <param name="arena">The arena the kill took place in.</param>
        /// <param name="killer">The player who made the kill.</param>
        /// <param name="killed">The player who got killed.</param>
        /// <param name="bounty">The number displayed in the kill message.</param>
        /// <param name="flags">The number of flags the killed player was carrying.</param>
        /// <param name="pts">The total number of points the kill was worth.</param>
        /// <param name="green">The default kill green.</param>
        /// <returns></returns>
        int KillGreen(Arena arena, Player killer, Player killed, int bounty, int flags, int pts, Prize green);
    }
}
