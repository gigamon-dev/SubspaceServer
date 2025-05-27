using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player changes the ship for a slot in a Team Versus match.
    /// </summary>
    /// <remarks>
    /// This is executed synchronously when the player's ship/freq is set.
    /// Therefore, handlers MUST NOT perform any additional ship/freq changes as that would be recursive and cause issues.
    /// </remarks>
    [CallbackHelper]
    public static partial class TeamVersusMatchPlayerShipChangedCallback
    {
        /// <summary>
        /// Delegate for when a player changes ship in a team versus match.
        /// </summary>
        /// <param name="playerSlot">The slot that the ship was changed for.</param>
        /// <param name="oldShip">The slot's previous ship.</param>
        /// <param name="newShip">The slot's new ship.</param>
        public delegate void TeamVersusMatchPlayerShipChangedDelegate(IPlayerSlot playerSlot, ShipType oldShip, ShipType newShip);
    }
}
