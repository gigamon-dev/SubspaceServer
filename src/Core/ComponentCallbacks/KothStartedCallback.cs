using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    public static class KothStartedCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game is started.
        /// </summary>
        /// <param name="arena">The arena the game was started in.</param>
        /// <param name="initialCrownedPlayers">The players that initially got a crown.</param>
        public delegate void KothStartedDelegate(Arena arena, IReadOnlySet<Player> initialCrownedPlayers);

        public static void Register(ComponentBroker broker, KothStartedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, KothStartedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, IReadOnlySet<Player> initialCrownedPlayers)
        {
            broker?.GetCallback<KothStartedDelegate>()?.Invoke(arena, initialCrownedPlayers);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, initialCrownedPlayers);
        }
    }
}
