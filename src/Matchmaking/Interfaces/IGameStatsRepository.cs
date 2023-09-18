using SS.Core;

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
        Task<long?> SaveGameAsync(Stream jsonStream);

        /// <summary>
        /// Gets the current rank of player(s).
        /// </summary>
        /// <param name="playerRatingDictionary">
        /// A dictionary of the player(s) to get the rank of where the key is the player name and the value is the rating.
        /// The dictionary's comparer must be <see cref="StringComparer.OrdinalIgnoreCase"/>.
        /// The rating will be updated for the player(s) that data could be retrieved for.
        /// </param>
        /// <returns></returns>
        Task GetPlayerRatingsAsync(Dictionary<string, int> playerRatingDictionary);

        // TODO: Add MMR functionality
        //Task GetPlayerMMRsAsync(Dictionary<string, int> playerRatingDictionary);
    }
}
