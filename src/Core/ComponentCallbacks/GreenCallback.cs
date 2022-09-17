using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="GreenDelegate"/> callback.
    /// </summary>
    public static class GreenCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> picks up a "green" (prize).
        /// </summary>
        /// <param name="player">The player that picked up a prize.</param>
        /// <param name="x">The x-coordinate.</param>
        /// <param name="y">The y-coordinate.</param>
        /// <param name="prize">The type of prize picked up.</param>
        public delegate void GreenDelegate(Player player, int x, int y, Prize prize);

        public static void Register(ComponentBroker broker, GreenDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, GreenDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, int x, int y, Prize prize)
        {
            broker?.GetCallback<GreenDelegate>()?.Invoke(player, x, y, prize);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, x, y, prize);
        }
    }
}
