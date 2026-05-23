using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class PlayerLatencyStatsUpdatedCallback
    {
        /// <summary>
        /// Callback delegate for when a player's client reported latency stats have been updated.
        /// </summary>
        /// <remarks>
        /// The client reported latency stats are accessible using <see cref="ILagQuery.QueryClientPing(Player, out ClientPingSummary)"/>.
        /// Also, the weapon counts for packetloss are also updated, accessible using <see cref="ILagQuery.QueryPacketloss(Player, out PacketlossSummary, out PacketlossDetails)"/>.
        /// </remarks>
        /// <param name="player">The player that the stats have been updated for.</param>
        public delegate void PlayerLatencyStatsUpdatedDelegate(Player player);
    }
}
