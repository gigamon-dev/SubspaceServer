using SS.Core;
using SS.Matchmaking.League;
using System.Collections.ObjectModel;

namespace SS.Matchmaking.Interfaces
{
    public interface ILeagueRepository : IComponentInterface
    {
        /// <summary>
        /// Starts a league game.
        /// </summary>
        /// <param name="seasonGameId">ID of the game to start.</param>
        /// <param name="forceStart">Whether to force starting the game (when it's already been started before).</param>
        /// <param name="cancellationToken"></param>
        /// <returns>A tuple containing the return status and, upon success, info about the league game.</returns>
        Task<(GameStartStatus Status, LeagueGameInfo?)> StartGameAsync(long seasonGameId, bool forceStart, CancellationToken cancellationToken);

        /// <summary>
        /// Saves game stats to the database.
        /// </summary>
        /// <param name="jsonStream">
        /// JSON representing the game stats.
        /// The format differs for each game mode (e.g. solo games, slotted team games, ball games, ...).
        /// See the database function documentation for details.
        /// </param>
        /// <returns>The resulting gameId from the database. <see langword="null"/> if there was an error saving.</returns>
        Task<long?> SaveGameAsync(long seasonGameId, Stream jsonStream);

        /// <summary>
        /// Prints the league schedule.
        /// </summary>
        /// <param name="playerName">The player to send the league schedule to.</param>
        /// <param name="seasonId">Optional, the season to get the schedule for. When <see langword="null"/>, the schedule for all open seasons is printed.</param>
        /// <param name="activeMatches">Optional, dictionary of known active matches. Used to print out the arena name to ?go to.</param>
        /// <returns></returns>
        Task PrintScheduleAsync(string playerName, long? seasonId, ReadOnlyDictionary<long, ILeagueMatch>? activeMatches);

        /// <summary>
        /// Prints league standings.
        /// </summary>
        /// <param name="playerName">The player to send the league standings to.</param>
        /// <param name="seasonId">The season to get standings for.</param>
        /// <returns></returns>
        Task PrintStandingsAsync(string playerName, long seasonId);

        /// <summary>
        /// Prints league results of a team.
        /// </summary>
        /// <param name="playerName">The player to send the league results to.</param>
        /// <param name="seasonId">The season to get the results for.</param>
        /// <returns></returns>
        Task PrintResultsAsync(string playerName, long seasonId, string teamName);

        /// <summary>
        /// Print a team's roster.
        /// </summary>
        /// <param name="playerName">The player to send the roster to.</param>
        /// <param name="seasonId">The season to get the results for.</param>
        /// <param name="teamName">The name of the team to get the roster for.</param>
        /// <returns></returns>
        Task PrintRosterAsync(string playerName, long seasonId, string teamName);
    }
}
