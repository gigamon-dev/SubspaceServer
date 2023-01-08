using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a team versus match starts.
    /// </summary>
    public static class TeamVersusMatchStartedCallback
    {
        /// <summary>
        /// Delegate for when a team versus match starts.
        /// </summary>
        /// <param name="matchData">The match that started.</param>
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
