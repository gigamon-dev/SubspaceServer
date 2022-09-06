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
        /// <param name="queue">The queue to register.</param>
        /// <returns></returns>
        bool RegisterQueue(IMatchmakingQueue queue);

        /// <summary>
        /// Removes a previously registered queue.
        /// </summary>
        /// <param name="queue">The queue to unregister.</param>
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
        /// Removes the 'Playing' state of the players/groups and if automatic requeuing was enabled for those players/groups, they will be requeued in the order provided.
        /// </summary>
        /// <param name="toUnset">A list of the players and groups to unset.</param>
        void UnsetPlaying(List<PlayerOrGroup> toUnset);

        /// <summary>
        /// Removes the 'Playing' state of a player that was previously marked with <see cref="SetPlaying(HashSet{Player}, HashSet{IPlayerGroup})"/>
        /// without allowing automatic re-queuing.
        /// </summary>
        /// <param name="player">The player to unset from the 'Playing' state.</param>
        void UnsetPlayingWithoutRequeue(Player player);

        /// <summary>
        /// Removes the 'Playing' state of a group that was previously marked with <see cref="SetPlaying(HashSet{Player}, HashSet{IPlayerGroup})"/>
        /// without allowing automatic re-queuing.
        /// </summary>
        /// <param name="group">The group to unset from the 'Playing' state.</param>
        void UnsetPlayingWithoutRequeue(IPlayerGroup group);
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
