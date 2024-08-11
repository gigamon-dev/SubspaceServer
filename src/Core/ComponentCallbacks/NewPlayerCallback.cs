using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="NewPlayerDelegate"/> callback.
    /// </summary>
    public static class NewPlayerCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> is allocated or deallocated.
        /// In general you probably want to use the <see cref="PlayerActionCallback.PlayerActionDelegate"/> 
        /// callback instead of this for general initialization tasks.
        /// </summary>
        /// <param name="player">The player being allocated or deallocated.</param>
        /// <param name="isNew">True if being allocated, false if being deallocated.</param>
        public delegate void NewPlayerDelegate(Player player, bool isNew);

        public static void Register(IComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, bool isNew)
        {
            broker?.GetCallback<NewPlayerDelegate>()?.Invoke(player, isNew);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, isNew);
        }
    }
}
