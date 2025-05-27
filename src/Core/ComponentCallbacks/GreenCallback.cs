using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="GreenDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class GreenCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> picks up a "green" (prize).
        /// </summary>
        /// <param name="player">The player that picked up a prize.</param>
        /// <param name="x">The x-coordinate.</param>
        /// <param name="y">The y-coordinate.</param>
        /// <param name="prize">The type of prize picked up.</param>
        public delegate void GreenDelegate(Player player, int x, int y, Prize prize);
    }
}
