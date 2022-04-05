using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagGainDelegate"/> callback.
    /// </summary>
    public class FlagGainCallback
    {
        /// <summary>
        /// Delegate for when the flag game is reset in an arena.
        /// </summary>
        /// <param name="arena">The arena the flag game was reset for.</param>
        public delegate void FlagGainDelegate(Arena arena, Player player, short flagId, FlagPickupReason reason);

        public static void Register(ComponentBroker broker, FlagGainDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FlagGainDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            broker?.GetCallback<FlagGainDelegate>()?.Invoke(arena, player, flagId, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, flagId, reason);
        }
    }
}
