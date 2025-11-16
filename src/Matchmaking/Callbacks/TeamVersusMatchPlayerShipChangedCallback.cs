using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for a player ship change in a Team Versus match.
    /// </summary>
    /// <remarks>
    /// This is executed synchronously when the player's ship/freq is set.
    /// Therefore, handlers MUST NOT perform any additional ship/freq changes as that would be recursive and cause issues.
    /// </remarks>
    /// <param name="playerSlot">The slot that the ship was changed for.</param>
    /// <param name="oldShip">The slot's previous ship.</param>
    /// <param name="newShip">The slot's new ship.</param>
    [ComponentCallback]
    public delegate void TeamVersusMatchPlayerShipChangedCallback(IPlayerSlot playerSlot, ShipType oldShip, ShipType newShip);
}
