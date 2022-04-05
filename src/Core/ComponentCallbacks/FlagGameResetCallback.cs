namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagGameResetDelegate"/> callback.
    /// </summary>
    public class FlagGameResetCallback
    {
        /// <summary>
        /// Delegate for when the flag game is reset in an arena.
        /// </summary>
        /// <param name="arena">The arena the flag game was reset for.</param>
        public delegate void FlagGameResetDelegate(Arena arena, short winnerFreq, int points);

        public static void Register(ComponentBroker broker, FlagGameResetDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FlagGameResetDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, short winnerFreq, int points)
        {
            broker?.GetCallback<FlagGameResetDelegate>()?.Invoke(arena, winnerFreq, points);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, winnerFreq, points);
        }
    }
}
