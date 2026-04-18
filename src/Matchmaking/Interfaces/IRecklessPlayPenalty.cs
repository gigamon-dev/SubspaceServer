using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for querying whether a player has a pending reckless play penalty for a match.
    /// </summary>
    public interface IRecklessPlayPenalty : IComponentInterface
    {
        /// <summary>
        /// Returns <see langword="true"/> if the specified player has a pending reckless play penalty
        /// recorded for the given match (i.e. they were KO'd too quickly and the match has not yet ended).
        /// </summary>
        bool HasPendingPenalty(IMatchData matchData, string playerName);
    }
}
