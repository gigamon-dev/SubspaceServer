using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a team versus match starts.
    /// </summary>
    [CallbackHelper]
    public static partial class TeamVersusMatchStartedCallback
    {
        /// <summary>
        /// Delegate for when a team versus match starts.
        /// </summary>
        /// <param name="matchData">The match that started.</param>
        public delegate void TeamVersusMatchStartedDelegate(IMatchData matchData);
    }
}
