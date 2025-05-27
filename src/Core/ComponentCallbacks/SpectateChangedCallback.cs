using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player changes who they're spectating.
    /// </summary>
    [CallbackHelper]
    public static partial class SpectateChangedCallback
    {
        /// <summary>
        /// Delegate for when a player changes who they're spectating.
        /// </summary>
        /// <param name="player">The player who's spectating state changed.</param>
        /// <param name="target">The player being spectated. <see langword="null"/> for removal.</param>
        public delegate void SpectateChangedDelegate(Player player, Player? target);
    }
}
