using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="KillDelegate"/> callback.
    /// </summary>
    public static class KillCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> kills another <see cref="Player"/>.
        /// </summary>
        /// <param name="arena">The arena the kill occurred in.</param>
        /// <param name="killer">The player that made the kill.</param>
        /// <param name="killed">The player that was killed.</param>
        /// <param name="bounty">The bounty of the <paramref name="killed"/> player</param>
        /// <param name="flagCount">The number of flags the <paramref name="killed"/> player was holding.</param>
        /// <param name="pts">The number of points awarded to the <paramref name="killer"/>.</param>
        /// <param name="green">The type of green prize dropped.</param>
        public delegate void KillDelegate(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green);

        public static void Register(ComponentBroker broker, KillDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, KillDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            broker?.GetCallback<KillDelegate>()?.Invoke(arena, killer, killed, bounty, flagCount, pts, green);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, killer, killed, bounty, flagCount, pts, green);
        }
    }
}
