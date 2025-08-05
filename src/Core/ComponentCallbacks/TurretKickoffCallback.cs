namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="TurretKickoffCallback"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class TurretKickoffCallback
    {
        /// <summary>
        /// Delegate for when a player kicks off attached players.
        /// </summary>
        /// <param name="player">The player that kicked off attached players.</param>
        public delegate void TurretKickoffDelegate(Player player);
    }
}
