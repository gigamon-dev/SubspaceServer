using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public struct PingSummary
    {
        public int Current, Average, Min, Max;
    }

    /// <summary>
    /// Client reported latency stat summary.
    /// </summary>
    public struct ClientPingSummary
    {
        /// <summary>
        /// Ping (ms)
        /// </summary>
        public int Current, Average, Min, Max;

        /// <summary>
        /// The average time (ms) difference between position packet times to the estimated server time, for the current interval.
        /// </summary>
        public int S2CAverageCurrent;

        /// <summary>
        /// The count of position packets that were considered to be slow, for the current interval.
        /// </summary>
        /// <remarks>
        /// A position packet is considered to be slow when the difference in the position packet time and the estimated server time is >= Latency:ClientSlowPacketTime (arena.conf setting).
        /// </remarks>
        public ushort S2CSlowCurrent;

        /// <summary>
        /// The count of position packets that were not considered to be slow based on Latency:ClientSlowPacketTime, for the current interval.
        /// </summary>
        /// <remarks><inheritdoc cref="S2CSlowCurrent" path="/remarks"/></remarks>
        public ushort S2CFastCurrent;

        /// <summary>
        /// The count of position packets that were considered slow based on Latency:ClientSlowPacketTime, 
        /// for all intervals, excluding the <see cref="S2CSlowCurrent">current interval</see>.
        /// </summary>
        /// <remarks><inheritdoc cref="S2CSlowCurrent" path="/remarks"/></remarks>
        public uint S2CSlowTotal;

        /// <summary>
        /// The count of position packets that were not considered slow based on Latency:ClientSlowPacketTime,
        /// for all intervals, excluding the <see cref="S2CFastCurrent">current interval</see>.
        /// </summary>
        /// <remarks><inheritdoc cref="S2CSlowCurrent" path="/remarks"/></remarks>
        public uint S2CFastTotal;
    }

    public struct PacketlossSummary
    {
        public double S2C, C2S, S2CWeapon;
    }

    public struct PacketlossDetails
    {
        public uint ServerPacketsSent, ClientPacketsReceived;
        public uint ClientPacketsSent, ServerPacketsReceived;
        public uint WeaponSentCount, WeaponReceiveCount;
    }

    public record struct TimeSyncRecord()
    {
        public required uint ServerTime;
        public required uint ClientTime;
    }

    public record struct PingHistogramBucket()
    {
        public required int Start;
        public required int End;
        public required int Count;
    }

    /// <summary>
    /// Interface for querying player lag data.
    /// </summary>
    public interface ILagQuery : IComponentInterface
    {
        /// <summary>
        /// Gets a player's ping info (from position packets).
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="ping">The data.</param>
        void QueryPositionPing(Player player, out PingSummary ping);

        /// <summary>
        /// Get a player's ping info (reported by the client).
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="ping">The data.</param>
        void QueryClientPing(Player player, out ClientPingSummary ping);

        /// <summary>
        /// Gets a player's ping info (from reliable packets).
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="ping">The data.</param>
        void QueryReliablePing(Player player, out PingSummary ping);

        /// <summary>
        /// Gets a <paramref name="player"/>'s packetloss <paramref name="summary"/>.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="summary">The packetloss summary.</param>
        void QueryPacketloss(Player player, out PacketlossSummary summary);

        /// <summary>
        /// Gets a <paramref name="player"/>'s packetloss <paramref name="summary"/> and <paramref name="details"/>.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="summary">The packetloss summary.</param>
        /// <param name="details">The packetloss details.</param>
        void QueryPacketloss(Player player, out PacketlossSummary summary, out PacketlossDetails details);

        /// <summary>
        /// Gets a player's reliable lag info.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="reliableLag">The data.</param>
        void QueryReliableLag(Player player, out ReliableLagData reliableLag);

        /// <summary>
        /// Gets a player's history of time sync requests (0x00 0x05 core packet).
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="records">A collection to be filled with a copy of the data.</param>
        void QueryTimeSyncHistory(Player player, ICollection<TimeSyncRecord> records);

        /// <summary>
        /// Gets a player's average drift in time syncs.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="timeDrift">The server calculated average drift based on time sync requests. <see langword="null"/> if not available (must have received a C2S security packet).</param>
        /// <param name="clientTimeDrift">The client reported drift. <see langword="null"/> if not available (must have received a C2S security packet, and Continuum only).</param>
        void QueryTimeSyncDrift(Player player, out int? timeDrift, out int? clientTimeDrift);

        /// <summary>
        /// Gets a player's ping histogram data based on C2S position packets.
        /// </summary>
        /// <param name="player">The player to get data for.</param>
        /// <param name="data">A collection to populate with data.</param>
        /// <returns><see langword="true"/> if <paramref name="data"/> was populated with data. Otherwise, <see langword="false"/>.</returns>
        bool GetPositionPingHistogram(Player player, ICollection<PingHistogramBucket> data);

        /// <summary>
        /// Gets a player's ping histogram data based on reliable packets.
        /// </summary>
        /// <param name="player">The player to get data for.</param>
        /// <param name="data">A collection to populate with data.</param>
        /// <returns><see langword="true"/> if <paramref name="data"/> was populated with data. Otherwise, <see langword="false"/>.</returns>
        bool GetReliablePingHistogram(Player player, ICollection<PingHistogramBucket> data);
    }
}
