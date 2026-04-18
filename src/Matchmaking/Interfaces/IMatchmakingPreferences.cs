using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.Matchmaking.Interfaces
{
    public enum MatchmakingMode
    {
        /// <summary>
        /// Default. No restrictions on skill disparity.
        /// </summary>
        Casual,

        /// <summary>
        /// Player prefers not to be placed in matches with large skill gaps.
        /// </summary>
        Strict,
    }

    public interface IMatchmakingPreferences : IComponentInterface
    {
        /// <summary>
        /// Gets the player's matchmaking mode preference.
        /// </summary>
        MatchmakingMode GetMatchmakingMode(string playerName);

        /// <summary>
        /// Sets the player's matchmaking mode. Returns the new mode.
        /// </summary>
        MatchmakingMode SetMatchmakingMode(Player player, MatchmakingMode mode);
    }
}
