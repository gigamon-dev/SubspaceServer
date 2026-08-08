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
        public readonly ushort S2CAverageCurrent { get; init; }
        public readonly ushort LastPing { get; init; }
        public readonly ushort AveragePing { get; init; }
        public readonly ushort LowestPing { get; init; }
        public readonly ushort HighestPing { get; init; }

        /// <remarks>
        /// Continuum only.
        /// <see langword="null"/> for VIE clients.
        /// </remarks>
        public readonly short? TimerDrift { get; init; }
    }

    /// <summary>
    /// Lag data from a time sync request (0x00 0x05).
    /// </summary>
    public readonly struct TimeSyncRequestData
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
        void Position(Player player, int ms, int? clientS2CPing);

        /// <summary>
        /// Increments the number of S2C weapon packets sent to a player since entering the arena.
        /// </summary>
        /// <param name="player">The player to update the data for.</param>
        void IncrementWeaponSentCount(Player player);

        /// <summary>
        /// Adds to the number of S2C weapon packets sent to a player since entering the arena.
        /// </summary>
        /// <param name="player">The player to update the data for.</param>
        /// <param name="value">The amount to add.</param>
        void AddWeaponSentCount(Player player, uint value);

        /// <summary>
        /// Stores the # of weapon packets that the server sent to the client since entering an arena, as of the start of a security check.
        /// This "pending" value is kept so that it can be used when the security response is received (<see cref="ClientLatency(Player, ref readonly ClientLatencyData)"/> is called), 
        /// rather than look at the current count which would include packets sent after the security request was sent.
        /// </summary>
        /// <param name="player">The player to update the data for.</param>
        void SetPendingWeaponSentCount(Player player);

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
        /// Collects information for when a C2S timesync request was received
        /// and if an optional follow-up S2C timesync request was sent along with the S2C timesync response.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="data">Data for the sync.</param>
        /// <param name="requestSent">Whether a S2C timesync request was sent at the same time as sending the response to the C2S timesync request.</param>
        void TimeSyncC2SRequestAndS2CRequest(Player player, ref readonly TimeSyncRequestData data, bool requestSent);

        /// <summary>
        /// Collects information for when a S2C timesync request was sent.
        /// </summary>
        /// <param name="player">The player the data is for.</param>
        /// <param name="serverRequestTime">The server time that the request was sent.</param>
        /// <param name="clientResponseTime">Optional, the client time if known (e.g. the S2C request is being sent in response to a C2S request which provided a client time).</param>
        void TimeSyncS2CRequest(Player player, uint serverRequestTime, uint? clientResponseTime);

        /// <summary>
        /// Collects information for when a time sync response was received.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="serverRequestTime">The server time when the request was made.</param>
        /// <param name="serverResponseTime">The server time when the response was received.</param>
        /// <param name="clientResponseTime">The client time when the client responded to the request.</param>
        void TimeSyncC2SResponse(Player player, uint serverRequestTime, uint serverResponseTime, uint clientResponseTime);

        /// <summary>
        /// Sets the C2S minimum latency estimate for fake players.
        /// </summary>
        /// <remarks>
        /// The C2S minimum latency estimate affects the time fields of outgoing position packets.
        /// <para>
        /// This is useful for replays. A replay can store what the player's estimate was, so that when played back, the equivalent position packet time can be sent.
        /// </para>
        /// <para>
        /// This could also potentially be useful for remotely controlled AI players that use their own custom communication channel rather than emulate being a client.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to set. This must be a <see cref="ClientType.Fake"/> player.</param>
        /// <param name="estimate">The C2S latency estimate (ticks).</param>
        void SetFakeC2SMinLatencyEstimate(Player player, uint estimate);

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
