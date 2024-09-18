using SS.Core.ComponentInterfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    [Flags]
    public enum ItemChanges
    {
        None = 0,
        Bursts = 1,
        Repels = 2,
        Thors = 4,
        Bricks = 8,
        Decoys = 16,
        Rockets = 32,
        Portals = 64,
    }

    public static class TeamVersusMatchPlayerItemsChangedCallback
    {
        public delegate void TeamVersusMatchPlayerItemsChangedDelegate(IPlayerSlot playerSlot, ItemChanges changes);

        public static void Register(IComponentBroker broker, TeamVersusMatchPlayerItemsChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, TeamVersusMatchPlayerItemsChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerSlot playerSlot, ItemChanges changes)
        {
            broker?.GetCallback<TeamVersusMatchPlayerItemsChangedDelegate>()?.Invoke(playerSlot, changes);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerSlot, changes);
        }
    }
}
