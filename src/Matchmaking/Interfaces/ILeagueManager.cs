using SS.Core;
using SS.Matchmaking.League;

namespace SS.Matchmaking.Interfaces
{
    public interface ILeagueManager : IComponentInterface
    {
        /// <summary>
        /// Registers a <paramref name="gameTypeId"/> to be managed by a specified <paramref name="gameMode"/>.
        /// </summary>
        /// <param name="gameTypeId">The game type to register.</param>
        /// <param name="gameMode">The game mode that will handle running games of the <paramref name="gameTypeId"/>.</param>
        /// <returns>Whether the registration was successfully added.</returns>
        bool Register(long gameTypeId, ILeagueGameMode gameMode);

        /// <summary>
        /// Removes a previous registration.
        /// </summary>
        /// <param name="gameTypeId">The game type to unregister.</param>
        /// <param name="gameMode">The game mode to unregister.</param>
        /// <returns>Whether the registration was successfully removed.</returns>
        bool Unregister(long gameTypeId, ILeagueGameMode gameMode);
    }

    public interface ILeagueGameMode
    {
        /// <summary>
        /// Creates a league match.
        /// </summary>
        /// <param name="leagueGameInfo">Information about the game.</param>
        /// <returns>The match if successfully created. <see langword="null"/> if there was a problem creating the match.</returns>
        ILeagueMatch? CreateMatch(LeagueGameInfo leagueGameInfo);
    }
}
