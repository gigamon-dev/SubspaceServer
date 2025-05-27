using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class KothStartedCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game is started.
        /// </summary>
        /// <param name="arena">The arena the game was started in.</param>
        /// <param name="initialCrownedPlayers">The players that initially got a crown.</param>
        public delegate void KothStartedDelegate(Arena arena, IReadOnlySet<Player> initialCrownedPlayers);
    }
}
