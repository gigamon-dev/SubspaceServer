using SS.Core.ComponentInterfaces;
using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    public static class KothWonCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game is won.
        /// </summary>
        /// <param name="arena">The arena the game was won in.</param>
        /// <param name="winners">The players that won the game.</param>
        /// <param name="points">The # of points awarded to each of the <paramref name="winners"/>.</param>
        public delegate void KothWonDelegate(Arena arena, IReadOnlySet<Player> winners, int points);

        public static void Register(IComponentBroker broker, KothWonDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, KothWonDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, IReadOnlySet<Player> winners, int points)
        {
            broker?.GetCallback<KothWonDelegate>()?.Invoke(arena, winners, points);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, winners, points);
        }
    }
}
