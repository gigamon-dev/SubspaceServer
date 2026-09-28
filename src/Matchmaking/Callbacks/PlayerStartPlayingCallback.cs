using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player enters the 'Playing' state.
    /// </summary>
    /// <param name="player">The player that entered the 'Playing' state.</param>
    [ComponentCallback]
    public delegate void PlayerStartPlayingCallback(Player player);
}
