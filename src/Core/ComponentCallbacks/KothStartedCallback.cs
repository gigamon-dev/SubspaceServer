using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callabck delegate for when a King of the Hill game has started.
    /// </summary>
    /// <param name="arena">The arena the game was started in.</param>
    /// <param name="initialCrownedPlayers">The players that initially got a crown.</param>
    [ComponentCallback]
    public delegate void KothStartedCallback(Arena arena, IReadOnlySet<Player> initialCrownedPlayers);
}
