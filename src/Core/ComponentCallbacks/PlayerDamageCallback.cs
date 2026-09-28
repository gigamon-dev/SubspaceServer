using SS.Packets.Game;
using SS.Utilities;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a watch damage packet is received.
    /// </summary>
    /// <param name="timestamp">Timestamp of when the damage was taken.</param>
    /// <param name="damageDataSpan">Detailed information about the damage taken.</param>
    [ComponentCallback]
    public delegate void PlayerDamageCallback(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan);
}
