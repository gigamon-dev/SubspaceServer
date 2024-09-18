using SS.Core.ComponentInterfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    public static class TeamVersusStatsPlayerKilledCallback
    {
        public delegate void TeamVersusStatsPlayerKilledDelegate(IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout);

        public static void Register(IComponentBroker broker, TeamVersusStatsPlayerKilledDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, TeamVersusStatsPlayerKilledDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout)
        {
            broker?.GetCallback<TeamVersusStatsPlayerKilledDelegate>()?.Invoke(killedSlot, killedStats, killerSlot, killerStats, isKnockout);

            if (broker?.Parent != null)
                Fire(broker.Parent, killedSlot, killedStats, killerSlot, killerStats, isKnockout);
        }
    }
}
