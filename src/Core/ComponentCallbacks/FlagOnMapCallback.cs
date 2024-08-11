using SS.Core.ComponentInterfaces;
using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagOnMapDelegate"/> callback.
    /// </summary>
    public class FlagOnMapCallback
    {
        /// <summary>
        /// Delegate for when a flag is placed on the map in a carry flag game.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="flagId">The ID of the flag that was placed.</param>
        /// <param name="mapCoordinate">The coordinates the flag was placed.</param>
        /// <param name="freq">The team the flag is owned by. -1 means unowned.</param>
        public delegate void FlagOnMapDelegate(Arena arena, short flagId, MapCoordinate mapCoordinate, short freq);

        public static void Register(IComponentBroker broker, FlagOnMapDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, FlagOnMapDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, short flagId, MapCoordinate mapCoordinate, short freq)
        {
            broker?.GetCallback<FlagOnMapDelegate>()?.Invoke(arena, flagId, mapCoordinate, freq);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, flagId, mapCoordinate, freq);
        }
    }
}
