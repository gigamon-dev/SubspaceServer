using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface that allows external modules to signal that a KO'd player was early-requeued into a new match,
    /// so that the old match's end-of-match cleanup skips calling <c>UnsetPlayingByName</c> for them.
    /// </summary>
    public interface IKoEarlyRequeue : IComponentInterface
    {
        /// <summary>
        /// Marks that the specified player was early-requeued out of the given match.
        /// End-of-match cleanup for that match will then skip removing the player from the Playing state,
        /// preserving their entry for any newer match they have joined.
        /// </summary>
        void MarkPlayerKoEarlyRequeued(IMatchData matchData, string playerName);
    }
}
