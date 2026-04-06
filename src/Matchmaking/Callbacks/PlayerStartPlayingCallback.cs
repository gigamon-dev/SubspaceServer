using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player enters the 'Playing' state.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerStartPlayingCallback
    {
        public delegate void PlayerStartPlayingDelegate(Player player);
    }
}
