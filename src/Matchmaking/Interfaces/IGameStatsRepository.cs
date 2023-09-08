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
        Task<long?> SaveGame(Stream jsonStream);
    }
}
