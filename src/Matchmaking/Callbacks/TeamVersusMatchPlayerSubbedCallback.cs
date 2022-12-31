using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    public static class TeamVersusMatchPlayerSubbedCallback
    {
        public delegate void TeamVersusPlayerSlotSubbedDelegate(IPlayerSlot playerSlot, string oldPlayerName);

        public static void Register(ComponentBroker broker, TeamVersusPlayerSlotSubbedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, TeamVersusPlayerSlotSubbedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IPlayerSlot playerSlot, string oldPlayerName)
        {
            broker?.GetCallback<TeamVersusPlayerSlotSubbedDelegate>()?.Invoke(playerSlot, oldPlayerName);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerSlot, oldPlayerName);
        }
    }
}
