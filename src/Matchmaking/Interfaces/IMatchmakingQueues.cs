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

        #region SetPlaying methods

        /// <summary>
        /// Marks a player as 'Playing'.
        /// </summary>
        /// <param name="player">The player to mark as 'Playing'.</param>
        void SetPlaying(Player player);

        /// <summary>
        /// Marks a set of players as 'Playing'.
        /// </summary>
        /// <param name="players">The players to mark as 'Playing'.</param>
        void SetPlaying<T>(T players) where T : IReadOnlyCollection<Player>;

        /// <summary>
        /// Marks a player as 'Playing' as a substitute player in an ongoing match.
        /// </summary>
        /// <remarks>
        /// Players that sub into existing matches do not lose their position in queues that they were searching on prior to subbing in.
        /// This method tells the service to remember that the player is playing as a substitute, so that when unset from playing, it will be able
        /// to restore the player's previous position(s) in those queue(s).
        /// </remarks>
        /// <param name="player">The player to mark as 'Playing'.</param>
        void SetPlayingAsSub(Player player);

        #endregion

        #region UnsetPlaying* methods

        /// <summary>
        /// Removes the 'Playing' state of a player such that the player will automatically be requeued into any queue(s) 
        /// they were previously searching on prior to getting set to 'Playing', and keep their previous position in each queue(s).
        /// </summary>
        /// <remarks>
        /// This is useful for when the player was set to 'Playing', but the game was cancelled before it could start.
        /// For example, if unable to start because another player they were matched up to play with/against disconnected before the game could start.
        /// </remarks>
        /// <param name="player">The player to change the state of.</param>
        void UnsetPlayingDueToCancel(Player player);

        /// <summary>
        /// Removes the 'Playing' state of a set of players such that the players will automatically be requeued into any queue(s) 
        /// they were previously searching on prior to getting set to 'Playing', and keep their previous position in each queue(s).
        /// </summary>
        /// <remarks>
        /// This is useful for when players were set to 'Playing', but the game was cancelled before it could start.
        /// For example, if unable to start because another player they were matched up to play with/against disconnected before the game could start.
        /// </remarks>
        /// <param name="players">The players to change the state of.</param>
        void UnsetPlayingDueToCancel<T>(T players) where T : IReadOnlyCollection<Player>;

        /// <summary>
        /// Removes the 'Playing' state of a player.
        /// </summary>
        /// <param name="player">The player to unset from the 'Playing' state.</param>
        /// <param name="allowRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlaying(Player player, bool allowRequeue);

        /// <summary>
        /// Removes the 'Playing' state of players.
        /// </summary>
        /// <remarks>
        /// The players are processed in the order provided. So, those earlier in the collection will be queued before those that come later.
        /// It is the job of the modules calling this to maintain the order of players, preferably keeping players that queued earlier to stay in front.
        /// </remarks>
        /// <param name="players">The players to unset from the 'Playing' state. The players are processed in the order provided.</param>
        /// <param name="allowRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlaying<T>(T players, bool allowRequeue) where T : IReadOnlyCollection<Player>;

        /// <summary>
        /// Removes the 'Playing' state of a player, by player name.
        /// </summary>
        /// <remarks>
        /// This overload is for when holding onto <see cref="Player"/> objects is not possible.
        /// For example, a module that holds players in the 'Playing' state until the match ends, even if a player disconnects.
        /// </remarks>
        /// <param name="playerName">The name of the player to unset from the 'Playing' state.</param>
        /// <param name="allowRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlayingByName(string playerName, bool allowRequeue);

        /// <summary>
        /// Removes the 'Playing' state of a set of players, by player name.
        /// </summary>
        /// <remarks>
        /// This overload is for when holding onto <see cref="Player"/> objects is not possible.
        /// For example, a module that holds players in the 'Playing' state until the match ends, even if a player disconnects.
        /// </remarks>
        /// <typeparam name="T">A collection of player names.</typeparam>
        /// <param name="playerNames">The names of players to unset from the 'Playing' state.</param>
        /// <param name="allowAutoRequeue">Whether to allow automatic re-queuing (search for another match).</param>
        void UnsetPlayingByName<T>(T playerNames, bool allowAutoRequeue) where T : IReadOnlyCollection<string>;

        /// <summary>
        /// Removes the 'Playing' state from a player and penalizes the player by placing a hold that prevents joining another match for a specified <paramref name="duration"/>.
        /// </summary>
        /// <remarks>
        /// This can be useful for penalizing a player for:
        /// <list type="bullet">
        ///     <item>not readying up or disconnecting before a match starts</item>
        ///     <item>
        ///         leaving a match without being subbed, preferably called when the match ends so that the <paramref name="duration"/> is relative to the match ending time.
        ///     </item>
        /// </list>
        /// <para>
        /// A hold persists even if the player disconnects and reconnects.
        /// For example, if a player has 1 minute remaining and disconnects, 
        /// the remaining duration will be restored when the player reconnects.
        /// This should help dissuade players from switching names to avoid penalties.
        /// </para>
        /// </remarks>
        /// <param name="playerName">The name of the player to affect.</param>
        /// <param name="duration">How long to place a hold for.</param>
        void UnsetPlayingWithHold(string playerName, TimeSpan duration);

        /// <summary>
        /// Removes the 'Playing' state from a set of players and penalizes them with holds that prevent them from joining another match for a specified <paramref name="duration"/>
        /// </summary>
        /// <remarks>
        /// <inheritdoc cref="UnsetPlayingWithHold(string, TimeSpan)" path="/remarks"/>
        /// <para>This method is generic to prevent boxing of the collection enumerator.</para>
        /// </remarks>
        /// <typeparam name="T">A collection of player names.</typeparam>
        /// <param name="playerNames">The names of the players to affect.</param>
        /// <param name="duration">How long to place a hold for.</param>
        void UnsetPlayingWithHold<T>(T playerNames, TimeSpan duration) where T : IReadOnlyCollection<string>;

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
