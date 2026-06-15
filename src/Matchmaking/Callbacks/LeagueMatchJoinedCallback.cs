using SS.Core;
using SS.Matchmaking.League;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player joins a league match for a team they're rostered on.
    /// </summary>
    [CallbackHelper]
    public static partial class LeagueMatchJoinedCallback
    {
        /// <summary>
        /// Delegate for when a player joins a league match for a team they're rostered on.
        /// </summary>
        /// <remarks>
        /// This does not mean the player is playing in the match. It just means the player joined their team's league match's arena.
        /// This can occur:
        /// when the player enters an arena that is hosting a league match for their team.
        /// OR
        /// when a league match for the the player's team is announced and the player is already in the arena.
        /// </remarks>
        /// <param name="arena">The arena that the league match is being hosted in.</param>
        /// <param name="player">The player that joined.</param>
        /// <param name="match">The league match.</param>
        public delegate void LeagueMatchJoinedDelegate(Arena arena, Player player, ILeagueMatch match);
    }
}
