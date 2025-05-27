using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player is subbed in for a team versus match.
    /// </summary>
    [CallbackHelper]
    public static partial class TeamVersusMatchPlayerSubbedCallback
    {
        /// <summary>
        /// Delegate for when a player is subbed in for a team versus match.
        /// </summary>
        /// <param name="playerSlot">The slot the player was subbed in to.</param>
        /// <param name="subOutPlayerName">The name of the player that was subbed out. <see langword="null"/> for a prevously unassigned slot.</param>
        public delegate void TeamVersusMatchPlayerSubbedDelegate(IPlayerSlot playerSlot, string? subOutPlayerName);
    }
}
