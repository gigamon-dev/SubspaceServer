namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class CrownToggledCallback
    {
        /// <summary>
        /// Delegate for when a player's crown is toggled.
        /// </summary>
        /// <param name="player">The player whose crown was toggled.</param>
        /// <param name="on">True if the crown was turned on. False if the crown was turned off.</param>
        public delegate void CrownToggledDelegate(Player player, bool on);
    }
}
