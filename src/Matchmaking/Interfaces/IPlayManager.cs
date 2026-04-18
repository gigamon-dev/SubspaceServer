using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for a service that keeps track of whether players are 'Playing' in a match 
    /// and whether players have 'Play Hold' penalties that prevent them from joining a match.
    /// </summary>
    public interface IPlayManager : IComponentInterface
    {
        /// <summary>
        /// Gets whether a <paramref name="player"/> is currently in the 'Playing' state (assigned to a match).
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <returns><see langword="true"/> if the player is playing in a match; otherwise, <see langword="false"/>.</returns>
        bool IsPlaying(Player player);

        #region SetPlaying* methods

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
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
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
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
        void UnsetPlayingWithHold<T>(T playerNames, TimeSpan duration) where T : IReadOnlyCollection<string>;

        #endregion

        #region Play Hold methods

        /// <summary>
        /// Penalizes a player with a 'Play Hold' for a <paramref name="duration"/>.
        /// If there is an existing hold, the <paramref name="duration"/> is added to it.
        /// </summary>
        /// <param name="player">The player to penalize.</param>
        /// <param name="duration">The amount of time to add as a penalty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="player"/> cannot be <see langword="null">.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
        void AddPlayHold(Player player, TimeSpan duration);

        /// <summary>
        /// Penalizes a player with a 'Play Hold' for a <paramref name="duration"/>.
        /// If there is an existing hold, the <paramref name="duration"/> is added to it.
        /// </summary>
        /// <param name="playerName">The name of the player to penalize.</param>
        /// <param name="duration">The amount of time to add as a penalty.</param>
        /// <exception cref="ArgumentException"><paramref name="playerName"/> cannot be <see langword="null"> or white-space.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
        void AddPlayHold(string playerName, TimeSpan duration);

        /// <summary>
        /// Penalizes a player with a 'Play Hold' for a <paramref name="duration"/>.
        /// If there is an existing hold, this replaces it.
        /// Use <see cref="AddPlayHold(string, TimeSpan)"/> to add to an existing hold rather than replace it.
        /// </summary>
        /// <param name="player">The player to penalize.</param>
        /// <param name="duration">The amount of time to set as a penalty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="player"/> cannot be <see langword="null">.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
        void SetPlayHold(Player player, TimeSpan duration);

        /// <summary>
        /// Penalizes a player with a 'Play Hold' for a <paramref name="duration"/>.
        /// If there is an existing hold, this replaces it.
        /// Use <see cref="AddPlayHold(string, TimeSpan)"/> to add to an existing hold rather than replace it.
        /// </summary>
        /// <param name="playerName">The name of the player to penalize.</param>
        /// <param name="duration">The amount of time to set as a penalty.</param>
        /// <exception cref="ArgumentException"><paramref name="playerName"/> cannot be <see langword="null"> or white-space.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> must be greater than 0.</exception>
        void SetPlayHold(string playerName, TimeSpan duration);

        /// <summary>
        /// Gets the timestamp a <paramref name="player"/>'s 'Play Hold' expires.
        /// </summary>
        /// <param name="player">The player to get play hold info for.</param>
        /// <exception cref="ArgumentNullException"><paramref name="player"/> cannot be <see langword="null">.</exception>
        /// <returns><see langword="null"/> if there no 'Play Hold'. Otherwise, the expiration timestamp (UTC).</returns>
        DateTime? GetPlayHoldExpiration(Player player);

        #endregion
    }
}
