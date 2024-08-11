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
        /// Delegate for when a flag is lost in a carry flag game.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player that lost the flag.</param>
        /// <param name="flagId">The ID of the flag that was lost.</param>
        /// <param name="reason">The reason the flag was lost.</param>
        public delegate void FlagLostDelegate(Arena arena, Player player, short flagId, FlagLostReason reason);

        public static void Register(IComponentBroker broker, FlagLostDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, FlagLostDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            broker?.GetCallback<FlagLostDelegate>()?.Invoke(arena, player, flagId, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, flagId, reason);
        }
    }
}
