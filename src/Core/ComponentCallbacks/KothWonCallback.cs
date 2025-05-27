using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class KothWonCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game is won.
        /// </summary>
        /// <param name="arena">The arena the game was won in.</param>
        /// <param name="winners">The players that won the game.</param>
        /// <param name="points">The # of points awarded to each of the <paramref name="winners"/>.</param>
        public delegate void KothWonDelegate(Arena arena, IReadOnlySet<Player> winners, int points);
    }
}
