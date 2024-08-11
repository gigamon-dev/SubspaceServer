using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public static class CrownToggledCallback
    {
        /// <summary>
        /// Delegate for when a player's crown is toggled.
        /// </summary>
        /// <param name="player">The player whose crown was toggled.</param>
        /// <param name="on">True if the crown was turned on. False if the crown was turned off.</param>
        public delegate void CrownToggledDelegate(Player player, bool on);

        public static void Register(IComponentBroker broker, CrownToggledDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, CrownToggledDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, bool on)
        {
            broker?.GetCallback<CrownToggledDelegate>()?.Invoke(player, on);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, on);
        }
    }
}
