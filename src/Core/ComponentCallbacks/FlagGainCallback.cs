using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagGainDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class FlagGainCallback
    {
        /// <summary>
        /// Delegate for when a flag is gained in a carry flag game.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player that gained the flag.</param>
        /// <param name="flagId">The ID of the flag that was gained.</param>
        /// <param name="reason">The reason the flag was gained.</param>
        public delegate void FlagGainDelegate(Arena arena, Player player, short flagId, FlagPickupReason reason);
    }
}
