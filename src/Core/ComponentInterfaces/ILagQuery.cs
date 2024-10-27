using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public struct PingSummary
    {
        public int Current, Average, Min, Max;
    }

    public struct ClientPingSummary
    {
        public int Current, Average, Min, Max;
        public uint S2CSlowTotal, S2CFastTotal;
        public ushort S2CSlowCurrent, S2CFastCurrent;
    }

    public struct PacketlossSummary
    {
        public double S2C, C2S, S2CWeapon;
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
        /// Gets a player's packetloss info.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <param name="packetloss">The data</param>
        void QueryPacketloss(Player player, out PacketlossSummary packetloss);

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
        /// Gets a player's average drift in time sync request.
        /// </summary>
        /// <param name="player">The player to get data about.</param>
        /// <returns>The average drift in time sync. <see langword="null"/> if not available.</returns>
        int? QueryTimeSyncDrift(Player player);

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
