using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for a service that manages matchmaking queues.
    /// </summary>
    public interface IMatchmakingQueues : IComponentInterface
    {
        /// <summary>
        /// Register's a queue, making it available to the ?next command.
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="options"></param>
        /// <param name="description"></param>
        /// <returns></returns>
        bool RegisterQueue(IMatchmakingQueue queue);

        /// <summary>
        /// Removes a previously registered queue.
        /// </summary>
        /// <param name="queueName"></param>
        /// <returns></returns>
        bool UnregisterQueue(IMatchmakingQueue queue);

        //void SetMatchStarted(Player player); // TODO: The player has been assigned a game to play in. Stops any searches. Set the player's queue state to 'Playing'.
        //void SetMatchComplete(Player player); // TODO: The player has completed their game. Set the player's queue state to 'None'. Allow to search for another match.
    }
}
