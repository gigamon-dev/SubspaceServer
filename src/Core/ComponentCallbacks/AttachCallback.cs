namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="AttachDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class AttachCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> attaches or detaches.
        /// </summary>
        /// <param name="player">The player that is attaching or detaching.</param>
        /// <param name="to">The player being attached to, or <see langword="null"/> when detaching.</param>
        public delegate void AttachDelegate(Player player, Player? to);
    }
}
