using SS.Core;

namespace SS.Matchmaking.Advisors
{
    /// <summary>
    /// Advisor interface that game mode modules can implement so that the <see cref="Modules.MatchFocus"/> module can be used.
    /// </summary>
    public interface IMatchFocusAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Asks for the names of players that are playing in a match.
        /// </summary>
        /// <remarks>
        /// This method uses player names since a player can disconnect from the server while in a match.
        /// </remarks>
        /// <param name="match">The match to get player names for.</param>
        /// <param name="players">A set to add player names to.</param>
        /// <returns>Whether the match was found and players provided.</returns>
        bool TryGetPlaying(IMatch match, HashSet<string> players);

        /// <summary>
        /// Asks for the match that a player is currently playing in.
        /// </summary>
        /// <param name="player">The player to get the match for.</param>
        /// <returns>The match that the player is playing in. <see langword="null"/> if none, not known.</returns>
        IMatch? GetMatch(Player player);
    }
}
