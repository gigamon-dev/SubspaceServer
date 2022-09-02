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

        /// <summary>
        /// Marks the players state as 'Playing'.
        /// For those marked as 'Playing', searches will disabled, and any ongoing ones are stopped.
        /// </summary>
        /// <param name="soloPlayers"></param>
        /// <param name="groups"></param>
        void SetPlaying(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups);

        /// <summary>
        /// Removes the 'Playing' state of the players/groups.
        /// This allows the player or groups to queue up again.
        /// If a player or group has automatic requeue enabled, that player or group will be requeued.
        /// </summary>
        /// <param name="toUnset">A list of the players and groups to unset. They will be reset in the order provided.</param>
        void UnsetPlaying(List<PlayerOrGroup> toUnset);
    }

    public class PlayerOrGroup
    {
        public PlayerOrGroup(Player player)
        {
            Player = player;
            Group = null;
        }

        public PlayerOrGroup(IPlayerGroup group)
        {
            Player = null;
            Group = group;
        }

        public Player Player { get; }
        public IPlayerGroup Group { get; }
    }
}
