using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ArenaActionDelegate"/> callback.
    /// </summary>
    public static class ArenaActionCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when an <see cref="Arena"/>'s life-cycle state has changed.
        /// </summary>
        /// <param name="arena">The arena whose state has changed.</param>
        /// <param name="action">The new state.</param>
        public delegate void ArenaActionDelegate(Arena arena, ArenaAction action);

        public static void Register(IComponentBroker broker, ArenaActionDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, ArenaActionDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, ArenaAction action)
        {
            broker?.GetCallback<ArenaActionDelegate>()?.Invoke(arena, action);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, action);
        }
    }
}
