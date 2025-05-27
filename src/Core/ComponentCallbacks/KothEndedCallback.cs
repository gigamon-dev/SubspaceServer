namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class KothEndedCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game has ended.
        /// </summary>
        /// <param name="arena">The arena the game was ended in.</param>
        public delegate void KothEndedDelegate(Arena arena);
    }
}
