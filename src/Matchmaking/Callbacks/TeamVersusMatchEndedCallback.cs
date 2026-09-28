using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a Team Versus match has ended.
    /// </summary>
    /// <param name="matchData">The match that ended.</param>
    /// <param name="reason">The reason the match ended.</param>
    /// <param name="winnerTeam">The team that won. <see langword="null"/> when there was no winner.</param>
    [ComponentCallback]
    public delegate void TeamVersusMatchEndedCallback(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam);
}
