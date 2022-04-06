using SS.Core.ComponentCallbacks;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that provides functionality to watch for damage on players.
    /// </summary>
    public interface IWatchDamage : IComponentInterface
    {
        /// <summary>
        /// Subscribes a <paramref name="player"/> to get damage notifications when a <paramref name="target"/> player takes damage.
        /// </summary>
        /// <param name="player">The player to subscribe to notifications.</param>
        /// <param name="target">The player being watched.</param>
        /// <returns></returns>
        bool TryAddWatch(Player player, Player target);

        /// <summary>
        /// Unsubscribes a <paramref name="player"/> from getting damage notifications when a <paramref name="target"/> player takes damage.
        /// </summary>
        /// <param name="player">The player to unsubscribe from notifications.</param>
        /// <param name="target">The player being watched.</param>
        /// <returns></returns>
        bool TryRemoveWatch(Player player, Player target);

        /// <summary>
        /// Removes all of a player's subscriptions for watching damage others players take, and optionally subscriptions of other players watching the player.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="includeWatchesOnPlayer"></param>
        void ClearWatch(Player player, bool includeWatchesOnPlayer);

        /// <summary>
        /// Adds a subscription for the <see cref="PlayerDamageCallback"/> on a player.
        /// </summary>
        /// <remarks>
        /// The <see cref="PlayerDamageCallback"/> will only be fired if there is at least one subscription.
        /// Also, remember to <see cref="RemoveCallbackWatch(Player)"/> when done.
        /// </remarks>
        /// <param name="player">The player to subscribe for.</param>
        void AddCallbackWatch(Player player);

        /// <summary>
        /// Removes a subscription for the <see cref="PlayerDamageCallback"/> on a player.
        /// </summary>
        /// <param name="player">The player to unsubscribe for.</param>
        void RemoveCallbackWatch(Player player);

        /// <summary>
        /// Gets the watch counts on a player.
        /// </summary>
        /// <param name="player">The player to get watch counts about.</param>
        /// <param name="playersWatching">The # of players watching the player.</param>
        /// <param name="callbackWatchCount">The # of callback watches on the player.</param>
        /// <returns></returns>
        bool TryGetWatchCount(Player player, out int playersWatching, out int callbackWatchCount);
    }
}
