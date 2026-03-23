using SS.Core;
using SS.Matchmaking.OpenSkill;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for a service that saves game stats to persistent storage, such as a database.
    /// </summary>
    public interface IGameStatsRepository : IComponentInterface
    {
        /// <summary>
        /// Saves game stats to the database.
        /// </summary>
        /// <param name="jsonStream">
        /// JSON representing the game stats.
        /// The format differs for each game mode (e.g. solo games, slotted team games, ball games, ...).
        /// See the database function documentation for details.
        /// </param>
        /// <returns>The resulting gameId from the database. <see langword="null"/> if there was an error saving.</returns>
        /// <exception cref="InvalidOperationException">The module is not loaded.</exception>
        Task<long?> SaveGameAsync(Stream jsonStream);

        /// <summary>
        /// Gets the current rank of player(s).
        /// </summary>
        /// <param name="gameTypeId">The game type to get ranks of players for.</param>
        /// <param name="ratings">
        /// A dictionary of the player(s) to get the rank of where the key is the player name and the value is the rating.
        /// The dictionary's comparer must be <see cref="StringComparer.OrdinalIgnoreCase"/>.
        /// The rating will be updated for the player(s) that data could be retrieved for.
        /// </param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"><paramref name="ratings"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="ratings"/> must use StringComparer.OrdinalIgnoreCase.</exception>
        /// <exception cref="InvalidOperationException">The module is not loaded.</exception>
        Task GetPlayerRatingsAsync(long gameTypeId, Dictionary<string, int> ratings);

        /// <summary>
        /// Gets the current OpenSkill ratings of players for a given game type.
        /// </summary>
        /// <param name="gameTypeId">The Id of the game type to get ratings for.</param>
        /// <param name="ratings">A dictionary of players and the rating objects to populate.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"><paramref name="ratings"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="ratings"/> must use StringComparer.OrdinalIgnoreCase.</exception>
        /// <exception cref="InvalidOperationException">The module is not loaded.</exception>
        /// <exception cref="Exception">Database error.</exception>
        Task GetPlayerOpenSkillRatingsAsync(long gameTypeId, Dictionary<string, PlayerRating> ratings);
    }
}
