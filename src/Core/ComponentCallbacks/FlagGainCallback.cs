using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagGainDelegate"/> callback.
    /// </summary>
    public class FlagGainCallback
    {
        /// <summary>
        /// Delegate for when a flag is gained in a carry flag game.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player that gained the flag.</param>
        /// <param name="flagId">The ID of the flag that was gained.</param>
        /// <param name="reason">The reason the flag was gained.</param>
        public delegate void FlagGainDelegate(Arena arena, Player player, short flagId, FlagPickupReason reason);

        public static void Register(IComponentBroker broker, FlagGainDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, FlagGainDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            broker?.GetCallback<FlagGainDelegate>()?.Invoke(arena, player, flagId, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, flagId, reason);
        }
    }
}
