using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a player is subbed for a Team Versus match.
    /// </summary>
    /// <param name="playerSlot">The slot the player was subbed in to.</param>
    /// <param name="subOutPlayerName">The name of the player that was subbed out. <see langword="null"/> for a prevously unassigned slot.</param>
    [ComponentCallback]
    public delegate void TeamVersusMatchPlayerSubbedCallback(IPlayerSlot playerSlot, string? subOutPlayerName);
}
