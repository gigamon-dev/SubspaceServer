using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagOnMapDelegate"/> callback.
    /// </summary>
    public class FlagOnMapCallback
    {
        /// <summary>
        /// Delegate for when the flag game is reset in an arena.
        /// </summary>
        /// <param name="arena">The arena the flag game was reset for.</param>
        public delegate void FlagOnMapDelegate(Arena arena, short flagId, MapCoordinate mapCoordinate, short freq);

        public static void Register(ComponentBroker broker, FlagOnMapDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FlagOnMapDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, short flagId, MapCoordinate mapCoordinate, short freq)
        {
            broker?.GetCallback<FlagOnMapDelegate>()?.Invoke(arena, flagId, mapCoordinate, freq);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, flagId, mapCoordinate, freq);
        }
    }
}
