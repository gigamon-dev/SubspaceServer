using SS.Core;

namespace SS.Matchmaking.Advisors
{
    public interface IMatchmakingQueueAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Gets the default queue for an arena.
        /// A matchmaking module can define a default queue for the arena(s) it manages.
        /// </summary>
        /// <param name="arena">The arena to get the default queue for.</param>
        /// <returns>The name of the default queue. Otherwise, null.</returns>
        string GetDefaultQueue(Arena arena) => null;

        /// <summary>
        /// Gets a queue name by an arena's alias.
        /// E.g., in the 1v1 (dueling arena) there are many boxes each with their own queue. ?next 1-7 
        /// </summary>
        /// <param name="arena">The arena to get the alias for.</param>
        /// <param name="alias">The alias to check.</param>
        /// <returns>The queue name if one was found. Otherwise, null.</returns>
        string GetQueueNameByAlias(Arena arena, string alias) => null;

        /// <summary>
        /// Determines whether a player is allowed to get added to a queue.
        /// 
        /// Possible uses:
        /// - a matchmaking module would keep track of the players currently in a match and disallow
        /// - a matchmaking module might only allow permitted players to queue into its queue (e.g. only registered league players for a 4v4squad match)
        /// </summary>
        /// <remarks>
        /// A player will not be allowed to search for a match if any advisor says to not allow.
        /// </remarks>
        /// <param name="player"></param>
        /// <returns></returns>
        bool AllowNext(Player player, string queueName) => true;
    }
}
