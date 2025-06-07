using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    [Flags]
    public enum MatchFocusReasons
    {
        None = 0,

        /// <summary>
        /// The player is/was a participant of the match.
        /// </summary>
        Participant = 1,

        /// <summary>
        /// The player is currently playing in the match.
        /// </summary>
        Playing = 2, 

        /// <summary>
        /// The player is spectating the match.
        /// </summary>
        Spectating = 4, 

        /// <summary>
        /// Any of the available reasons.
        /// </summary>
        Any = Participant | Playing | Spectating,
    }

    /// <summary>
    /// Interface for a service that tracks the association of players with ongoing matches, 
    /// whether it be because a player: participated in the match, is playing in the match, or is spectating the match.
    /// </summary>
    public interface IMatchFocus : IComponentInterface
    {
        /// <summary>
        /// Get players associated with a specified <paramref name="match"/>.
        /// </summary>
        /// <param name="match">The match to get players for.</param>
        /// <param name="players">A set to add players to.</param>
        /// <param name="filterReasons">Optionally, filter to only include only players that are associated to the match for specific reasons. <see langword="null"/> to not filter.</param>
        /// <param name="arenaFilter">Optionally, filter to only include players that are currently in a specific arena. <see langword="null"/> to not filter.</param>
        /// <returns><see langword="true"/> if the match was found and <paramref name="players"/> provided. <see langword="false"/> if the match was not found.</returns>
        bool TryGetPlayers(IMatch match, HashSet<Player> players, MatchFocusReasons? filterReasons, Arena? arenaFilter);

        /// <summary>
        /// Gets the match the player is currently playing in.
        /// </summary>
        /// <param name="player">The player to get the match for.</param>
        /// <returns>The match the player is playing in. <see langword="null"/> if the player is not playing in a match.</returns>
        IMatch? GetPlayingMatch(Player player);

        /// <summary>
        /// Gets the match the player is currently focused on.
        /// </summary>
        /// <remarks>
        /// If the player is spectating a match, the match the player is spectating is returned.
        /// Otherwise, if the player is playing in a match, the match that the player is playing in is returned.
        /// </remarks>
        /// <param name="player">The player to get the match for.</param>
        /// <returns>The currently focused match. <see langword="null"/> if there is no currently focused match.</returns>
        IMatch? GetFocusedMatch(Player player);
    }
}
