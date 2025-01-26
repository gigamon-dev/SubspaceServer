namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Client reported lag data from <see cref="Packets.Game.C2S_Security"/>.
    /// </summary>
    public readonly struct ClientLatencyData
    {
        public readonly uint WeaponCount { get; init; }
        public readonly uint S2CSlowTotal { get; init; }
        public readonly uint S2CFastTotal { get; init; }
        public readonly ushort S2CSlowCurrent { get; init; }
        public readonly ushort S2CFastCurrent { get; init; }
        public readonly ushort Unknown1 { get; init; }
        public readonly ushort LastPing { get; init; }
        public readonly ushort AveragePing { get; init; }
        public readonly ushort LowestPing { get; init; }
        public readonly ushort HighestPing { get; init; }
    }

    /// <summary>
    /// Lag data from a time sync request (0x00 0x05).
    /// </summary>
    public readonly struct TimeSyncData
    {
        /* what the server thinks */
        public readonly uint ServerPacketsReceived { get; init; }
        public readonly uint ServerPacketsSent { get; init; }

        /* what the client reports */
        public readonly uint ClientPacketsReceived { get; init; }
        public readonly uint ClientPacketsSent { get; init; }

        /* time sync */
        public readonly uint ServerTime { get; init; }
        public readonly uint ClientTime { get; init; }
    }

    public readonly struct ReliableLagData
    {
        /// <inheritdoc cref="Modules.Network.ConnData.RelDups" path="/summary"/>
        public readonly ulong RelDups { get; init; }

        /// <inheritdoc cref="Modules.Network.ConnData.AckDups" path="/summary"/>
        public readonly ulong AckDups { get; init; }

        /// <inheritdoc cref="Modules.Network.ConnData.ReliablePacketsReceived" path="/summary"/>
        public readonly uint ReliablePacketsReceived { get; init; }

        /// <inheritdoc cref="Modules.Network.ConnData.Retries" path="/summary"/>
        public readonly ulong Retries { get; init; }

        /// <inheritdoc cref="Modules.Network.ConnData.ReliablePacketsSent" path="/summary"/>
        public readonly uint ReliablePacketsSent { get; init; }
    }

    /// <summary>
    /// Interface for collecting player lag data.
    /// </summary>
    public interface ILagCollect : IComponentInterface
    {
        /// <summary>
        /// For collecting information when a client sends a position packet.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="ms">The one-way time (difference between the server's time and the client's time from the position packet) in milliseconds.</param>
        /// <param name="clientS2CPing">The S2C ping in milliseconds reported by the client in a position packet's extra position data. <see langword="null"/> for position packets without extra position data.</param>
        /// <param name="serverWeaponCount">The number of S2C weapon packets sent to a player since entering the arena.</param>
        void Position(Player player, int ms, int? clientS2CPing, uint serverWeaponCount);

        /// <summary>
        /// For collecting information when a reliable acknowledgement packet arrives.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="ms">The roundtrip time (difference in the current server time and the time the reliable packet was last sent) in milliseconds.</param>
        void RelDelay(Player player, int ms);

        /// <summary>
        /// For collecting information when a client responds to a security check.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="data">The data reported by the client in the <see cref="Packets.C2S_Security"/> packet.</param>
        void ClientLatency(Player player, ref readonly ClientLatencyData data);

        /// <summary>
        /// For collecting information when a time sync request arrives (0x00 0x05 core packet).
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="data">Data for the sync.</param>
        void TimeSync(Player player, ref readonly TimeSyncData data);

        /// <summary>
        /// For collecting information after processing the outgoing network queues.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="data">The reliable data to record.</param>
        void RelStats(Player player, ref readonly ReliableLagData data);

        /// <summary>
        /// Clears previously collected data for a player.
        /// </summary>
        /// <param name="player">The player to clear data for.</param>
        void Clear(Player player);
    }
}
