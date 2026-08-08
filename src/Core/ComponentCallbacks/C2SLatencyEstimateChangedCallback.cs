namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class C2SLatencyEstimateChangedCallback
    {
        /// <summary>
        /// Delegate for when a player's C2S minimum latency estimate has changed.
        /// </summary>
        /// <remarks>
        /// This callback does not get executed on the mainloop thread.
        /// </remarks>
        /// <param name="player">The player that the estimate has changed for.</param>
        /// <param name="c2sLatency">The estimate.</param>
        public delegate void C2SLatencyEstimateChangedDelegate(Player player, uint c2sLatency);
    }
}
