namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="GlobalConfigChangedDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class GlobalConfigChangedCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when the global.conf file was changed.
        /// </summary>
        public delegate void GlobalConfigChangedDelegate();
    }
}
