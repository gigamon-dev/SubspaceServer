namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ArenaActionDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class ArenaActionCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when an <see cref="Arena"/>'s life-cycle state has changed.
        /// </summary>
        /// <param name="arena">The arena whose state has changed.</param>
        /// <param name="action">The new state.</param>
        public delegate void ArenaActionDelegate(Arena arena, ArenaAction action);
    }
}
