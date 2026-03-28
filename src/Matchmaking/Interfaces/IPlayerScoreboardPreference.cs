using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.Matchmaking.Interfaces
{
    public enum ScoreboardPreference
    {
        /// <summary>
        /// Show the full statbox with lives, repels, and rockets.
        /// </summary>
        Detailed,

        /// <summary>
        /// Show only kills and deaths columns.
        /// </summary>
        Simple,

        /// <summary>
        /// Hide the scoreboard entirely.
        /// </summary>
        Off,
    }

    public interface IPlayerScoreboardPreference : IComponentInterface
    {
        /// <summary>
        /// Gets the scoreboard display preference for a player.
        /// </summary>
        ScoreboardPreference GetPreference(Player player);
    }
}
