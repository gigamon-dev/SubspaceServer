using Microsoft.Extensions.ObjectPool;
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
        /// <param name="soloPlayers">Players that were queued as solo that are to be marked as 'Playing'.</param>
        /// <param name="groups">Groups that were queued that are to marked as 'Playing'.</param>
        void SetPlaying(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups);

        /// <summary>
        /// Removes the 'Playing' state of the players/groups and if automatic requeuing was enabled for those players/groups, they will be requeued in the order provided.
        /// </summary>
        /// <remarks>
        /// Use the <see cref="PlayerOrGroupListPool"/> to reduce allocations.
        /// </remarks>
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

        /// <summary>
        /// Object pool for <see cref="List{T}"/>s of <see cref="PlayerOrGroup"/>. For use with <see cref="UnsetPlaying(List{PlayerOrGroup})"/>.
        /// </summary>
        ObjectPool<List<PlayerOrGroup>> PlayerOrGroupListPool { get; }
    }

    /// <summary>
    /// A type that wraps either a <see cref="Core.Player"/> or <see cref="IPlayerGroup"/>.
    /// </summary>
    public struct PlayerOrGroup
    {
        public PlayerOrGroup(Player player)
        {
            Player = player ?? throw new ArgumentNullException(nameof(player));
            Group = null;
        }

        public PlayerOrGroup(IPlayerGroup group)
        {
            Player = null;
            Group = group ?? throw new ArgumentNullException(nameof(group));
        }

        public Player Player { get; }
        public IPlayerGroup Group { get; }
    }
}
