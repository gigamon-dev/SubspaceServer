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
        /// <param name="players">Players that are to be marked as 'Playing'.</param>
        void SetPlaying(HashSet<Player> players);

        /// <summary>
        /// Marks the player's state as 'Playing', as a substitute player in an ongoing match.
        /// This tells the service to keep track of the original timestamp that the player queued up
        /// so that the player can be requeued in the original position.
        /// </summary>
        /// <param name="player"></param>
        void SetPlayingAsSub(Player player);

        /// <summary>
        /// Removes the 'Playing' state of players that were previously marked with <see cref="SetPlaying(HashSet{Player})"/>, in the order provided.
        /// </summary>
        /// <param name="players">The players to unset from the 'Playing' state.</param>
        /// <param name="allowRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlaying<T>(T players, bool allowRequeue) where T : IReadOnlyCollection<Player>;

        /// <summary>
        /// Removes the 'Playing' state of a player that was previously marked with <see cref="SetPlaying(HashSet{Player})"/>.
        /// </summary>
        /// <param name="player">The player to unset from the 'Playing' state.</param>
        /// <param name="allowRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlaying(Player player, bool allowRequeue);

        /// <summary>
        /// Object pool for <see cref="List{T}"/>s of <see cref="PlayerOrGroup"/>. For use with <see cref="UnsetPlaying(List{PlayerOrGroup})"/>.
        /// </summary>
        //ObjectPool<List<PlayerOrGroup>> PlayerOrGroupListPool { get; }
    }

    /// <summary>
    /// A type that wraps either a <see cref="Core.Player"/> or <see cref="IPlayerGroup"/>.
    /// </summary>
    public readonly record struct PlayerOrGroup
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
