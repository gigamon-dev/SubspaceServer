using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a team versus match ends.
    /// </summary>
    [CallbackHelper]
    public static partial class TeamVersusMatchEndedCallback
    {
        /// <summary>
        /// Delegate for when a team versus match ends.
        /// </summary>
        /// <param name="matchData">The match that ended.</param>
        /// <param name="reason">The reason the match ended.</param>
        /// <param name="winnerTeam">The team that won. <see langword="null"/> when there was no winner.</param>
        public delegate void TeamVersusMatchEndedDelegate(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam);
    }
}
