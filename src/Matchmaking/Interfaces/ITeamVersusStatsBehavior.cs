using SS.Core;
using SS.Matchmaking.TeamVersus;
using SS.Utilities;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Behavior interface for extending the <see cref="Modules.TeamVersusMatch"/> module with logic for statistics.
    /// </summary>
    /// <remarks>
    /// The original design for statistics was to just listen to callback events and gather data.
    /// However, it eventually became apparent that the statistics logic would need to influence the 
    /// <see cref="Modules.TeamVersusMatch"/> module. For example, when a player is killed, it made more
    /// sense for the statistics module to send notificaitons to players as it would have more information,
    /// such as damage stats which could be used to display assists.
    /// Also, when a match ends, the statistics module might want to save information about the match to
    /// the database. Saving to the database would need to be done asynchronously, but that would require
    /// that the <see cref="Modules.TeamVersusMatch"/> module not tear down (reset) the match object model
    /// until saving is complete.
    /// </remarks>
    public interface ITeamVersusStatsBehavior : IComponentInterface
    {
        /// <summary>
        /// Balances teams based on player rating.
        /// </summary>
        /// <param name="matchConfiguration">The configuration of the match to balance teams for.</param>
        /// <param name="teamList">The teams to balance.</param>
        /// <returns>Whether teams were balanced.</returns>
        Task<bool> BalanceTeamsAsync(IMatchConfiguration matchConfiguration, IReadOnlyList<TeamLineup> teamList) => Task.FromResult(false);

        /// <summary>
        /// Initializes a match that is about to begin.
        /// This allows the stats module to initialize its object model for and do database work.
        /// </summary>
        /// <param name="matchData">The match being initialized.</param>
        /// <returns>A task that completes when the stats module is done initializing.</returns>
        Task InitializeAsync(IMatchData matchData);

        /// <summary>
        /// Called after a match has just started.
        /// This allows the stats module to do additional initializtion and database work.
        /// </summary>
        /// <param name="matchData">The match that started.</param>
        /// <returns></returns>
        ValueTask MatchStartedAsync(IMatchData matchData);

        //ValueTask PlayerSubbedAsync(IPlayerSlot playerSlot, string subOutPlayerName);

        //void PlayerUnassignedSlot(IPlayerSlot playerSlot)

        /// <summary>
        /// Called when a player in a match is killed.
        /// </summary>
        /// <param name="timestampTick">Tick count of when the kill was made.</param>
        /// <param name="timestamp">Timestamp of when the kill was made</param>
        /// <param name="matchData">The match data the kill is for.</param>
        /// <param name="killed">The player that was killed.</param>
        /// <param name="killedSlot">The slot of the player that was killed.</param>
        /// <param name="killer">The player that made the kill.</param>
        /// <param name="killerSlot">The slot of the player that made the kill.</param>
        /// <param name="isKnockout">Whether the kill was a knock out (killed player has no more lives left).</param>
        /// <returns><see langword="true"/> if chat notifications were sent. Otherwise, <see langword="false"/>.</returns>
        ValueTask<bool> PlayerKilledAsync(
            ServerTick timestampTick,
            DateTime timestamp,
            IMatchData matchData,
            Player killed,
            IPlayerSlot killedSlot,
            Player killer,
            IPlayerSlot killerSlot,
            bool isKnockout);

        /// <summary>
        /// Called when a match ends.
        /// Stats for the game can be saved to a database.
        /// Chat notifications can be sent.
        /// </summary>
        /// <param name="matchData">The match data of that match that ended.</param>
        /// <param name="reason">The reason the match ended.</param>
        /// <param name="winnerTeam">The team that won. <see langword="null"/> for no winner.</param>
        /// <returns><see langword="true"/> if chat notifications were sent. Otherwise, <see langword="false"/>.</returns>
        ValueTask<bool> MatchEndedAsync(IMatchData matchData, MatchEndReason reason, ITeam winnerTeam);
    }
}
