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
    }
}
