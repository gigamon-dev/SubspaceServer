using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    public static  class TeamVersusMatchEndedCallback
    {
        public delegate void TeamVersusMatchEndedDelegate(IMatchData matchData, MatchEndReason reason, int? winnerTeamIdx);

        public static void Register(ComponentBroker broker, TeamVersusMatchEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, TeamVersusMatchEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IMatchData matchData, MatchEndReason reason, int? winnerTeamIdx)
        {
            broker?.GetCallback<TeamVersusMatchEndedDelegate>()?.Invoke(matchData, reason, winnerTeamIdx);

            if (broker?.Parent != null)
                Fire(broker.Parent, matchData, reason, winnerTeamIdx);
        }
    }
}
