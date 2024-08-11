using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PlayerDamageDelegate"/> callback.
    /// </summary>
    public static class PlayerDamageCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a watch damage packet is received.
        /// </summary>
        /// <param name="timestamp">Timestamp of when the damage was taken.</param>
        /// <param name="damageDataSpan">Detailed information about the damage taken.</param>
        public delegate void PlayerDamageDelegate(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan);

        public static void Register(IComponentBroker broker, PlayerDamageDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, PlayerDamageDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            broker?.GetCallback<PlayerDamageDelegate>()?.Invoke(player, timestamp, damageDataSpan);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, timestamp, damageDataSpan);
        }
    }
}
