using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.Matchmaking.Interfaces
{
    public enum StatboxPreference
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
        /// Hide the statbox entirely.
        /// </summary>
        Off,
    }

    public interface IPlayerStatboxPreference : IComponentInterface
    {
        /// <summary>
        /// Gets the statbox display preference for a player.
        /// </summary>
        StatboxPreference GetPreference(Player player);
    }
}
