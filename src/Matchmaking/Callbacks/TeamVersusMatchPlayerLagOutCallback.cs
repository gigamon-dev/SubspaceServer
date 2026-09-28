using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a player lags out (leaves the arena or changes to spectator mode while in a match) in a Team Versus match.
    /// </summary>
    /// <param name="playerSlot">The slot for the player that lagged out.</param>
    /// <param name="reason">The reason for the lag out.</param>
    [ComponentCallback]
    public delegate void TeamVersusMatchPlayerLagOutCallback(IPlayerSlot playerSlot, SlotInactiveReason reason);
}
