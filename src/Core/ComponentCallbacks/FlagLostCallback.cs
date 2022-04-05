using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public enum FlagLostReason
    {
        /// <summary>
        /// Regular drop.
        /// </summary>
        Dropped,

        /// <summary>
        /// Dropped by a carrier in a safe zone.
        /// </summary>
        InSafe,

        /// <summary>
        /// Changed ship.
        /// </summary>
        ShipChange,

        /// <summary>
        /// Changed teams.
        /// </summary>
        FreqChange,

        /// <summary>
        /// Left the arena / exited the game.
        /// </summary>
        LeftArena,

        /// <summary>
        /// The carrier was killed.
        /// </summary>
        Killed,
    }

    public static class FlagLostReasonExtensions
    {
        public static FlagLostReason ToFlagLostReason(this AdjustFlagReason adjustFlagReason)
        {
            return (FlagLostReason)adjustFlagReason;
        }
    }

    /// <summary>
    /// Helper class for the <see cref="FlagLostDelegate"/> callback.
    /// </summary>
    public class FlagLostCallback
    {
        /// <summary>
        /// Delegate for when the flag game is reset in an arena.
        /// </summary>
        /// <param name="arena">The arena the flag game was reset for.</param>
        public delegate void FlagLostDelegate(Arena arena, Player player, short flagId, FlagLostReason reason);

        public static void Register(ComponentBroker broker, FlagLostDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FlagLostDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            broker?.GetCallback<FlagLostDelegate>()?.Invoke(arena, player, flagId, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, flagId, reason);
        }
    }
}
