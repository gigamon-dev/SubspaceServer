using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player changes who they're spectating.
    /// </summary>
    public static class SpectateChangedCallback
    {
        /// <summary>
        /// Delegate for when a player changes who they're spectating.
        /// </summary>
        /// <param name="player">The player who's spectating state changed.</param>
        /// <param name="target">The player being spectated. <see langword="null"/> for removal.</param>
        public delegate void SpectateChangedDelegate(Player player, Player? target);

        public static void Register(IComponentBroker broker, SpectateChangedDelegate handler) 
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, SpectateChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, Player? target)
        {
            broker?.GetCallback<SpectateChangedDelegate>()?.Invoke(player, target);

            if (broker?.Parent is not null)
                Fire(broker.Parent, player, target);
        }
    }
}
