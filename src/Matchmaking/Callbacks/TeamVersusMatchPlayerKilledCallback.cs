using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a player is killed in a Team Versus match.
    /// </summary>
    /// <param name="killedSlot"></param>
    /// <param name="killerSlot"></param>
    /// <param name="isKnockout"></param>
    [ComponentCallback]
    public delegate void TeamVersusMatchPlayerKilledCallback(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout);
}
