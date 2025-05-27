namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="MainloopDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class MainloopCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked once per iteration of the main loop.
        /// </summary>
        public delegate void MainloopDelegate();
    }
}
