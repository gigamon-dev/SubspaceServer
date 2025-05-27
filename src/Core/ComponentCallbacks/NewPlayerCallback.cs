namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="NewPlayerDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class NewPlayerCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> is allocated or deallocated.
        /// In general you probably want to use the <see cref="PlayerActionCallback.PlayerActionDelegate"/> 
        /// callback instead of this for general initialization tasks.
        /// </summary>
        /// <param name="player">The player being allocated or deallocated.</param>
        /// <param name="isNew">True if being allocated, false if being deallocated.</param>
        public delegate void NewPlayerDelegate(Player player, bool isNew);
    }
}
