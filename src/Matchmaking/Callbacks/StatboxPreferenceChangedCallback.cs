using SS.Core;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player changes their statbox preference.
    /// </summary>
    /// <param name="player">The player that changed their preference.</param>
    /// <param name="newPreference">The chosen preference.</param>
    [ComponentCallback]
    public delegate void StatboxPreferenceChangedCallback(Player player, StatboxPreference newPreference);
}
