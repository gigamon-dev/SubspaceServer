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

    /// <summary>
    /// Callback delegate for when the item count has changed for a player in a Team Versus match.
    /// </summary>
    /// <param name="playerSlot"></param>
    /// <param name="changes"></param>
    [ComponentCallback]
    public delegate void TeamVersusMatchPlayerItemsChangedCallback(IPlayerSlot playerSlot, ItemChanges changes);
}
