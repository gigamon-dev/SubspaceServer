using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    public static class TeamVersusMatchStartedCallback
    {
        public delegate void TeamVersusMatchStartedDelegate(IMatchData matchData);

        public static void Register(ComponentBroker broker, TeamVersusMatchStartedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, TeamVersusMatchStartedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IMatchData matchData)
        {
            broker?.GetCallback<TeamVersusMatchStartedDelegate>()?.Invoke(matchData);

            if (broker?.Parent != null)
                Fire(broker.Parent, matchData);
        }
    }
}
