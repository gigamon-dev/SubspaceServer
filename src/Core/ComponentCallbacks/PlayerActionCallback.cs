namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PlayerActionDelegate"/> callback.
    /// </summary>
    public static class PlayerActionCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/>'s life-cycle state changes.
        /// </summary>
        /// <param name="player">The player that changed state.</param>
        /// <param name="action">The new state.</param>
        /// <param name="arena">The <see cref="Arena"/> the player is in. <see langword="null"/> if the player is not in an <see cref="Arena"/>.</param>
        public delegate void PlayerActionDelegate(Player player, PlayerAction action, Arena arena);

        public static void Register(ComponentBroker broker, PlayerActionDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerActionDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, PlayerAction action, Arena arena)
        {
            broker?.GetCallback<PlayerActionDelegate>()?.Invoke(player, action, arena);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, action, arena);
        }
    }
}
