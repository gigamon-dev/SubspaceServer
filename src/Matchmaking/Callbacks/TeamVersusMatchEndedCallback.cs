using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a team versus match ends.
    /// </summary>
    public static  class TeamVersusMatchEndedCallback
    {
        /// <summary>
        /// Delegate for when a team versus match ends.
        /// </summary>
        /// <param name="matchData">The match that ended.</param>
        /// <param name="reason">The reason the match ended.</param>
        /// <param name="winnerTeam">The team that won. <see langword="null"/> when there was no winner.</param>
        public delegate void TeamVersusMatchEndedDelegate(IMatchData matchData, MatchEndReason reason, ITeam winnerTeam);

        public static void Register(ComponentBroker broker, TeamVersusMatchEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, TeamVersusMatchEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IMatchData matchData, MatchEndReason reason, ITeam winnerTeam)
        {
            broker?.GetCallback<TeamVersusMatchEndedDelegate>()?.Invoke(matchData, reason, winnerTeam);

            if (broker?.Parent != null)
                Fire(broker.Parent, matchData, reason, winnerTeam);
        }
    }
}
