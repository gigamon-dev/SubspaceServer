using SS.Packets.Game;
using SS.Utilities;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PlayerDamageDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerDamageCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a watch damage packet is received.
        /// </summary>
        /// <param name="timestamp">Timestamp of when the damage was taken.</param>
        /// <param name="damageDataSpan">Detailed information about the damage taken.</param>
        public delegate void PlayerDamageDelegate(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan);
    }
}
