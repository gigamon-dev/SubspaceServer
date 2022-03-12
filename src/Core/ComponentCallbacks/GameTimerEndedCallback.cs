namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper for the <see cref="GameTimerEndedCallback"/> callback.
    /// </summary>
    /// <remarks>
    /// Also consider using the <see cref="GameTimerChangedCallback"/>.
    /// </remarks>
    public static class GameTimerEndedCallback
    {
        public delegate void GameTimerEndedDelegate(Arena arena);

        public static void Register(ComponentBroker broker, GameTimerEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, GameTimerEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena)
        {
            broker?.GetCallback<GameTimerEndedDelegate>()?.Invoke(arena);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena);
        }
    }
}
