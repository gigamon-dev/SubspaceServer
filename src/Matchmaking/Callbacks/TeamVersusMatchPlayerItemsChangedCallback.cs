using SS.Core;
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

    [CallbackHelper]
    public static partial class TeamVersusMatchPlayerItemsChangedCallback
    {
        public delegate void TeamVersusMatchPlayerItemsChangedDelegate(IPlayerSlot playerSlot, ItemChanges changes);
    }
}
