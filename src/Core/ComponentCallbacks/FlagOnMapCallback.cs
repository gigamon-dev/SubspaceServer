using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagOnMapDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class FlagOnMapCallback
    {
        /// <summary>
        /// Delegate for when a flag is placed on the map in a carry flag game.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="flagId">The ID of the flag that was placed.</param>
        /// <param name="coordinates">The coordinates the flag was placed.</param>
        /// <param name="freq">The team the flag is owned by. -1 means unowned.</param>
        public delegate void FlagOnMapDelegate(Arena arena, short flagId, TileCoordinates coordinates, short freq);
    }
}
