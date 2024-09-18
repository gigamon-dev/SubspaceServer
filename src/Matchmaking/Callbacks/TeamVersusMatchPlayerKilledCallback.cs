using SS.Core.ComponentInterfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    public static class TeamVersusMatchPlayerKilledCallback
    {
        public delegate void TeamVersusPlayerKilledDelegate(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout);

        public static void Register(IComponentBroker broker, TeamVersusPlayerKilledDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, TeamVersusPlayerKilledDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout)
        {
            broker?.GetCallback<TeamVersusPlayerKilledDelegate>()?.Invoke(killedSlot, killerSlot, isKnockout);

            if (broker?.Parent != null)
                Fire(broker.Parent, killedSlot, killerSlot, isKnockout);
        }
    }
}
