using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a flag is gained in a carry flag game.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="player">The player that gained the flag.</param>
    /// <param name="flagId">The ID of the flag that was gained.</param>
    /// <param name="reason">The reason the flag was gained.</param>
    [ComponentCallback]
    public delegate void FlagGainCallback(Arena arena, Player player, short flagId, FlagPickupReason reason);
}
