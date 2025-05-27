using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="KillDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class KillCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> kills another <see cref="Player"/>.
        /// </summary>
        /// <param name="arena">The arena the kill occurred in.</param>
        /// <param name="killer">The player that made the kill.</param>
        /// <param name="killed">The player that was killed.</param>
        /// <param name="bounty">The bounty of the <paramref name="killed"/> player</param>
        /// <param name="flagCount">The number of flags the <paramref name="killed"/> player was holding.</param>
        /// <param name="points">The number of points awarded to the <paramref name="killer"/>.</param>
        /// <param name="green">The type of green prize dropped.</param>
        public delegate void KillDelegate(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green);
    }
}
