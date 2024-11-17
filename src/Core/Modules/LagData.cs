using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that tracks lag statistics of players.
    /// </summary>
    [CoreModuleInfo]
    public class LagData : IModule, ILagCollect, ILagQuery
    {
        // TODO: maybe some of these could be config settings?
        private const int MaxPing = 10000;
        private const int PacketlossMinPackets = 200;
        private const int MaxBucket = 25;
        private const int BucketWidth = 20;
        private const int TimeSyncSamples = 10;

        private readonly IComponentBroker _broker;
        private readonly IPlayerData _playerData;
        private InterfaceRegistrationToken<ILagCollect>? _iLagCollectToken;
        private InterfaceRegistrationToken<ILagQuery>? _iLagQueryToken;

        /// <summary>
        /// per player data key
        /// </summary>
        private PlayerDataKey<PlayerLagStats> _lagkey;

        public LagData(IComponentBroker broker, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            _lagkey = _playerData.AllocatePlayerData<PlayerLagStats>();

            _iLagCollectToken = _broker.RegisterInterface<ILagCollect>(this);
            _iLagQueryToken = _broker.RegisterInterface<ILagQuery>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLagCollectToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iLagQueryToken) != 0)
                return false;

            _playerData.FreePlayerData(ref _lagkey);

            return true;
        }

        #endregion

        #region ILagCollect Members

        void ILagCollect.Position(Player player, int ms, int? clientS2CPing, uint serverWeaponCount)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdatePositionStats(ms, clientS2CPing, serverWeaponCount);
        }

        void ILagCollect.RelDelay(Player player, int ms)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateReliableAckStats(ms);
        }

        void ILagCollect.ClientLatency(Player player, ref readonly ClientLatencyData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateClientLatencyStats(in data);
        }

        void ILagCollect.TimeSync(Player player, ref readonly TimeSyncData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateTimeSyncStats(in data);
        }

        void ILagCollect.RelStats(Player player, ref readonly ReliableLagData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateReliableStats(in data);
        }

        void ILagCollect.Clear(Player player)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.Reset();
        }

        #endregion

        #region ILagQuery Members

        void ILagQuery.QueryPositionPing(Player player, out PingSummary ping)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryPositionPing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryClientPing(Player player, out ClientPingSummary ping)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryClientPing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryReliablePing(Player player, out PingSummary ping)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryReliablePing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary packetloss)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryPacketloss(out packetloss);
            else
                packetloss = default;
        }

        void ILagQuery.QueryReliableLag(Player player, out ReliableLagData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryReliableLag(out data);
            else
                data = default;
        }

        void ILagQuery.QueryTimeSyncHistory(Player player, ICollection<TimeSyncRecord> records)
        {
            if (player is not null && records is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryTimeSyncHistory(records);
            else
                records?.Clear();
        }

        int? ILagQuery.QueryTimeSyncDrift(Player player)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return lagStats.QueryTimeSyncDrift();
            else
                return null;
        }

        bool ILagQuery.GetPositionPingHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return lagStats.GetPositionPingHistogram(data);
            else
                return false;
        }

        bool ILagQuery.GetReliablePingHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return lagStats.GetReliablePingHistogram(data);
            else
                return false;
        }

        #endregion

        #region Helper classes

        private class PingStats
        {
            /// <summary>
            /// Buckets for a histogram of pings.
            /// </summary>
            public int[] Buckets = new int[MaxBucket];

            /// <summary>
            /// Current ping in milliseconds
            /// </summary>
            public int Current;

            /// <summary>
            /// Average ping in milliseconds
            /// </summary>
            public int Average;

            /// <summary>
            /// Maximum ping in milliseconds
            /// </summary>
            public int Max;

            /// <summary>
            /// Minimum ping in milliseconds
            /// </summary>
            public int Min;

            public void Add(int ms)
            {
                // Prevent horribly incorrect pings from messing up stats.
                if (ms > MaxPing)
                    ms = MaxPing;

                if (ms < 0)
                    ms = 0;

                Current = ms;

                Buckets[MillisecondsToBucket(ms)]++;

                Average = (Average * 7 + ms) / 8; // modified moving average

                if (ms < Min)
                    Min = ms;

                if (ms > Max)
                    Max = ms;
            }

            public void Reset()
            {
                Array.Clear(Buckets);
                Current = 0;
                Average = 0;
                Max = 0;
                Min = 0;
            }

            private static int MillisecondsToBucket(int ms)
            {
                return (ms < 0) ? 0 : ((ms < (MaxBucket * BucketWidth)) ? (ms / BucketWidth) : MaxBucket - 1);
            }
        }

        private class TimeSyncHistory
        {
            private TimeSyncRecord[] _records = new TimeSyncRecord[TimeSyncSamples];
            private int _next = 0;
            private int _count = 0;

            public void Update(uint serverTime, uint clientTime)
            {
                int sampleIndex = _next;
                _records[sampleIndex].ServerTime = serverTime;
                _records[sampleIndex].ClientTime = clientTime;
                _next = (sampleIndex + 1) % _records.Length;

                if (_count < _records.Length)
                    _count++;
            }

            public void Reset()
            {
                Array.Clear(_records);
                _next = 0;
                _count = 0;
            }

            public int? Drift
            {
                get
                {
                    int drift = 0;
                    int count = 0;

                    for (int i = _count; i > 1; i--)
                    {
                        int j = (_next + _records.Length - i) % _records.Length;
                        int k = (_next + _records.Length - (i - 1)) % _records.Length;

                        int delta = (new ServerTick(_records[j].ServerTime) - new ServerTick(_records[j].ClientTime))
                            - (new ServerTick(_records[k].ServerTime) - new ServerTick(_records[k].ClientTime));

                        if (delta >= -10000 && delta <= 10000)
                        {
                            drift += delta;
                            count++;
                        }
                    }

                    return count > 0 ? drift / count : null;
                }
            }

            public void GetHistory(ICollection<TimeSyncRecord> records)
            {
                if (records is null)
                    return;

                records.Clear();

                for (int i = _count; i > 0; i--)
                {
                    records.Add(_records[(_next + _records.Length - i) % _records.Length]);
                }
            }
        }

        private class PlayerLagStats : IResettable
        {
            private PingStats PositionPacketPing = new();
            private PingStats ReliablePing = new();
            private ClientLatencyData ClientReportedPing;
            private TimeSyncData Packetloss;
            private TimeSyncHistory TimeSync = new();
            private ReliableLagData ReliableLagData;

            /// <summary>
            /// The latest # of weapon packets that the server sent to the client.
            /// </summary>
            private uint LastWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client, as of the last successful security check.
            /// </summary>
            private uint WeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the client reported it received, as of the last successful security check.
            /// </summary>
            private uint WeaponReceiveCount;

            private readonly Lock _lock = new();

            public void Reset()
            {
                lock (_lock)
                {
                    PositionPacketPing.Reset();
                    ReliablePing.Reset();
                    ClientReportedPing = default;
                    Packetloss = default;
                    TimeSync.Reset();
                    ReliableLagData = default;
                    LastWeaponSentCount = 0;
                    WeaponSentCount = 0;
                    WeaponReceiveCount = 0;
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }

            public void UpdatePositionStats(int ms, int? clientS2CPing, uint serverWeaponCount)
            {
                lock (_lock)
                {
                    PositionPacketPing.Add(ms * 2); // convert one-way to round-trip
                    LastWeaponSentCount = serverWeaponCount;

                    // TODO: do something with clientS2CPing?
                }
            }

            public void UpdateReliableAckStats(int ms)
            {
                lock (_lock)
                {
                    ReliablePing.Add(ms);
                }
            }

            public void UpdateClientLatencyStats(ref readonly ClientLatencyData data)
            {
                lock (_lock)
                {
                    ClientReportedPing = data;
                    WeaponReceiveCount = data.WeaponCount;
                    WeaponSentCount = LastWeaponSentCount;
                }
            }

            public void UpdateTimeSyncStats(ref readonly TimeSyncData data)
            {
                lock (_lock)
                {
                    Packetloss = data;
                    TimeSync.Update(data.ServerTime, data.ClientTime);
                }
            }

            public void UpdateReliableStats(ref readonly ReliableLagData data)
            {
                lock (_lock)
                {
                    ReliableLagData = data;
                }
            }

            public void QueryPositionPing(out PingSummary ping)
            {
                lock (_lock)
                {
                    ping.Current = PositionPacketPing.Current;
                    ping.Average = PositionPacketPing.Average;
                    ping.Min = PositionPacketPing.Min;
                    ping.Max = PositionPacketPing.Max;
                }
            }

            public void QueryClientPing(out ClientPingSummary ping)
            {
                lock (_lock)
                {
                    // ClientReportedPing is in ticks (centiseconds).  Convert to milliseconds.
                    ping.Current = ClientReportedPing.LastPing * 10;
                    ping.Average = ClientReportedPing.AveragePing * 10;
                    ping.Min = ClientReportedPing.LowestPing * 10;
                    ping.Max = ClientReportedPing.HighestPing * 10;

                    ping.S2CSlowTotal = ClientReportedPing.S2CSlowTotal;
                    ping.S2CFastTotal = ClientReportedPing.S2CFastTotal;
                    ping.S2CSlowCurrent = ClientReportedPing.S2CSlowCurrent;
                    ping.S2CFastCurrent = ClientReportedPing.S2CFastCurrent;
                }
            }

            public void QueryReliablePing(out PingSummary ping)
            {
                lock (_lock)
                {
                    ping.Current = ReliablePing.Current;
                    ping.Average = ReliablePing.Average;
                    ping.Min = ReliablePing.Min;
                    ping.Max = ReliablePing.Max;
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary)
            {
                lock (_lock)
                {
                    ulong s, r;

                    s = Packetloss.ServerPacketsSent;
                    r = Packetloss.ClientPacketsReceived;
                    summary.S2C = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;

                    s = Packetloss.ClientPacketsSent;
                    r = Packetloss.ServerPacketsReceived;
                    summary.C2S = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;

                    s = WeaponSentCount;
                    r = WeaponReceiveCount;
                    summary.S2CWeapon = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;
                }
            }

            public void QueryReliableLag(out ReliableLagData data)
            {
                lock (_lock)
                {
                    data = ReliableLagData;
                }
            }

            public void QueryTimeSyncHistory(ICollection<TimeSyncRecord> records)
            {
                lock (_lock)
                {
                    TimeSync.GetHistory(records);
                }
            }

            public int? QueryTimeSyncDrift()
            {
                lock (_lock)
                {
                    return TimeSync.Drift;
                }
            }

            public bool GetPositionPingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return GetPingHistogram(PositionPacketPing, data);
                }
            }

            public bool GetReliablePingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return GetPingHistogram(ReliablePing, data);
                }
            }

            private static bool GetPingHistogram(PingStats stats, ICollection<PingHistogramBucket> data)
            {
                if (stats is null || data is null)
                    return false;

                int endIndex = stats.Buckets.Length - 1;
                do
                {
                    if (stats.Buckets[endIndex] > 0)
                        break;
                }
                while (--endIndex >= 0);

                if (endIndex < 0)
                    return false;

                int i;
                for (i = 0; i <= endIndex; i++)
                {
                    if (stats.Buckets[i] > 0)
                        break;
                }

                data.Clear();

                for (; i <= endIndex; i++)
                {
                    data.Add(
                        new PingHistogramBucket()
                        {
                            Start = i * BucketWidth,
                            End = ((i + 1) * BucketWidth) - 1,
                            Count = stats.Buckets[i]
                        });
                }

                return true;
            }
        }

        #endregion
    }
}
