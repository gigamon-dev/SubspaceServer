using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Behavior interface for extending <see cref="Modules.CaptainsMatch"/> with statistics tracking and database persistence.
    /// </summary>
    public interface ICaptainsMatchStatsBehavior : IComponentInterface
    {
        /// <summary>
        /// Called when a captains match begins (both teams have readied up and the match is starting).
        /// </summary>
        /// <param name="arena">The arena the match is in.</param>
        /// <param name="freq1">The frequency of the first team.</param>
        /// <param name="team1">Players on the first team.</param>
        /// <param name="freq2">The frequency of the second team.</param>
        /// <param name="team2">Players on the second team.</param>
        void MatchStarted(Arena arena, short freq1, IEnumerable<Player> team1, short freq2, IEnumerable<Player> team2);

        /// <summary>
        /// Called when a player is killed during an active match.
        /// </summary>
        /// <param name="arena">The arena the kill took place in.</param>
        /// <param name="killer">The player who made the kill.</param>
        /// <param name="killed">The player who was killed.</param>
        void PlayerKilled(Arena arena, Player killer, Player killed);

        /// <summary>
        /// Called when a match ends. The implementation should save stats to the database.
        /// </summary>
        /// <param name="arena">The arena the match was in.</param>
        /// <param name="winnerFreq">The frequency of the winning team.</param>
        /// <param name="loserFreq">The frequency of the losing team.</param>
        Task MatchEndedAsync(Arena arena, short winnerFreq, short loserFreq);
    }
}
