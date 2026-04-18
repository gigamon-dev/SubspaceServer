using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for a service that manages matchmaking queues.
    /// </summary>
    public interface IMatchmakingQueues : IComponentInterface
    {
        #region Queue Registration

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
        /// Searches for a queue by <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name of the queue to find.</param>
        /// <returns>The queue, or <see langword="null"/> if not found.</returns>
        IMatchmakingQueue? GetQueue(ReadOnlySpan<char> name);

        #endregion

        #region Command Names

        /// <summary>
        /// The name of the command to start searching on matchmaking queue(s).
        /// </summary>
        string NextCommandName { get; }

        /// <summary>
        /// The name of the command to stop searching on matchmaking queues.
        /// </summary>
        string CancelCommandName { get; }

        #endregion
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

        public Player? Player { get; }
        public IPlayerGroup? Group { get; }
    }
}
