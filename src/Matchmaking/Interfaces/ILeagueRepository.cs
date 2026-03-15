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

        #region League Roles

        /// <summary>
        /// Requests a league 'Practice Permit' for a player.
        /// </summary>
        /// <param name="playerName">The player to request the permit for.</param>
        /// <param name="leagueId">The league to request the permit for.</param>
        /// <param name="byPlayerName">The name of the player submitting the request. This can be <see langword="null"/> for a self request.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>A tuple that may contain a RequestId and/or error message.</returns>
        /// <exception cref="Exception">Database error.</exception>
        Task<(long? RequestId, string? ErrorMessage)> RequestLeaguePermitAsync(string playerName, long leagueId, string? byPlayerName, CancellationToken cancellationToken);

        /// <summary>
        /// Prints pending 'Practice Permit' requests to a player via chat messages.
        /// </summary>
        /// <param name="playerName">The player to send results to.</param>
        /// <param name="leagueId">The league to get requests for.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        Task PrintPendingPermitRequestsAsync(string playerName, long leagueId);

        /// <summary>
        /// Grants a league role to a player.
        /// </summary>
        /// <param name="toPlayerName">The player to grant.</param>
        /// <param name="leagueId">The league to grant the role in.</param>
        /// <param name="role">The role to grant.</param>
        /// <param name="executorPlayerName">The player that is performing the action.</param>
        /// <param name="notes">Notes to store in the log.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception">Database error.</exception>
        Task<bool> InsertLeaguePlayerRoleAsync(ReadOnlyMemory<char> toPlayerName, long leagueId, LeagueRole role, string? executorPlayerName, ReadOnlyMemory<char> notes, CancellationToken cancellationToken);

        /// <summary>
        /// Removes a league role and/or role request from a player.
        /// </summary>
        /// <param name="fromPlayerName">The player to revoke.</param>
        /// <param name="leagueId">The league to revoke the role in.</param>
        /// <param name="role">The role to revoke.</param>
        /// <param name="executorPlayerName">The player that is performing the action.</param>
        /// <param name="notes">Notes to store in the log.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception">Database error.</exception>
        Task<bool> DeleteLeaguePlayerRoleAsync(ReadOnlyMemory<char> fromPlayerName, long leagueId, LeagueRole role, string? executorPlayerName, ReadOnlyMemory<char> notes, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the timestamp that a League + Role combination was last updated.
        /// </summary>
        /// <param name="leagueId">Id of the league to get data for.</param>
        /// <param name="role">The role to get data for.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<DateTime?> GetLeaguePlayerRoleLastUpdatedAsync(long leagueId, LeagueRole role, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the players that have a role for a league.
        /// </summary>
        /// <param name="leagueId">Id of the league to get data for.</param>
        /// <param name="role">The role to get data for.</param>
        /// <param name="grants">A set to populate with player names.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task GetLeaguePlayerRoleGrantsAsync(long leagueId, LeagueRole role, HashSet<string> grants, CancellationToken cancellationToken);

        #endregion
    }
}
