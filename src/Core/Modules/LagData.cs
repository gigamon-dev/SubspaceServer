using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;

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

        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<ILagCollect> _iLagCollectToken;
        private InterfaceRegistrationToken<ILagQuery> _iLagQueryToken;

        /// <summary>
        /// per player data key
        /// </summary>
        private int _lagkey;

        #region IModule Members

        public bool Load(ComponentBroker broker, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _lagkey = _playerData.AllocatePlayerData<PlayerLagStats>();

            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _iLagCollectToken = _broker.RegisterInterface<ILagCollect>(this);
            _iLagQueryToken = _broker.RegisterInterface<ILagQuery>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLagCollectToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iLagQueryToken) != 0)
                return false;

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            _playerData.FreePlayerData(_lagkey);

            return true;
        }

        #endregion

        #region ILagCollect Members

        void ILagCollect.Position(Player player, int ms, int? clientPing, uint serverWeaponCount)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.UpdatePositionStats(ms, clientPing, serverWeaponCount);
        }

        void ILagCollect.RelDelay(Player player, int ms)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.UpdateReliableAckStats(ms);
        }

        void ILagCollect.ClientLatency(Player player, in ClientLatencyData data)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.UpdateClientLatencyStats(in data);
        }

        void ILagCollect.TimeSync(Player player, in TimeSyncData data)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.UpdateTimeSyncStats(in data);
        }

        void ILagCollect.RelStats(Player player, in ReliableLagData data)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.UpdateReliableStats(in data);
        }

        void ILagCollect.Clear(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.Reset();
        }

        #endregion

        #region ILagQuery Members

        void ILagQuery.QueryPositionPing(Player player, out PingSummary ping)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryPositionPing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryClientPing(Player player, out ClientPingSummary ping)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryClientPing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryReliablePing(Player player, out PingSummary ping)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryReliablePing(out ping);
            else
                ping = default;
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary packetloss)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryPacketloss(out packetloss);
            else
                packetloss = default;
        }

        void ILagQuery.QueryReliableLag(Player player, out ReliableLagData data)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryReliableLag(out data);
            else
                data = default;
        }

        void ILagQuery.QueryTimeSyncHistory(Player player, in ICollection<(uint ServerTime, uint ClientTime)> history)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                lagStats.QueryTimeSyncHistory(in history);
            else
                history.Clear();
        }

        int ILagQuery.QueryTimeSyncDrift(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (player[_lagkey] is PlayerLagStats lagStats)
                return lagStats.QueryTimeSyncDrift();
            else
                return 0;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (player == null)
                return;

            if (player[_lagkey] is not PlayerLagStats lagStats)
                return;

            if (action == PlayerAction.Connect)
                lagStats.Reset();
        }

        #region Helper classes

        private class PingStats
        {
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
                Buckets.Initialize();
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
            private const int TimeSyncSamples = 10;

            public uint[] ServerTime = new uint[TimeSyncSamples];
            public uint[] ClientTime = new uint[TimeSyncSamples];
            private int next;

            public void Update(in TimeSyncData data)
            {
                int sampleIndex = next;
                ServerTime[sampleIndex] = data.ServerTime;
                ClientTime[sampleIndex] = data.ClientTime;
                next = (sampleIndex + 1) % TimeSyncSamples;
            }

            public void Reset()
            {
                ServerTime.Initialize();
                ClientTime.Initialize();
                next = 0;
            }

            public int Drift
            {
                get
                {
                    int drift = 0;
                    int count = 0;

                    for (int i = 0; i < TimeSyncSamples; i++)
                    {
                        int j = (i + next) % TimeSyncSamples;
                        int k = (i + next - 1) % TimeSyncSamples;
                        int delta = (new ServerTick(ServerTime[j]) - new ServerTick(ClientTime[j])) - (new ServerTick(ServerTime[k]) - new ServerTick(ClientTime[k]));
                        if (delta >= -10000 && delta <= 10000)
                        {
                            drift += delta;
                            count++;
                        }
                    }

                    return count != 0 ? drift / count : 0;
                }
            }

            public void GetHistory(in ICollection<(uint ServerTime, uint ClientTime)> history)
            {
                history.Clear();

                for (int i = 0; i < TimeSyncSamples; i++)
                {
                    history.Add((ServerTime[i], ClientTime[i]));
                }
            }
        }

        private class PlayerLagStats
        {
            public PingStats PositionPacketPing = new();
            public PingStats ReliablePing = new();
            public ClientLatencyData ClientReportedPing;
            public TimeSyncData Packetloss;
            public TimeSyncHistory TimeSync = new();
            public ReliableLagData ReliableLagData;

            /// <summary>
            /// The latest # of weapon packets that the server sent to the client.
            /// </summary>
            public uint LastWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client, as of the last successful security check.
            /// </summary>
            public uint WeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the client reported it received, as of the last successful security check.
            /// </summary>
            public uint WeaponReceiveCount;

            private readonly object lockObj = new();

            public void Reset()
            {
                lock (lockObj)
                {
                    PositionPacketPing.Reset();
                    ReliablePing.Reset();
                    ClientReportedPing = default;
                    Packetloss = default;
                    TimeSync.Reset();
                    ReliableLagData = default;
                }
            }

            public void UpdatePositionStats(int ms, int? clientPing, uint serverWeaponCount)
            {
                lock (lockObj)
                {
                    PositionPacketPing.Add(ms * 2); // convert one-way to round-trip
                    LastWeaponSentCount = serverWeaponCount;

                    // TODO: do something with clientPing?
                }
            }

            public void UpdateReliableAckStats(int ms)
            {
                lock (lockObj)
                {
                    ReliablePing.Add(ms);
                }
            }

            public void UpdateClientLatencyStats(in ClientLatencyData data)
            {
                lock (lockObj)
                {
                    ClientReportedPing = data;
                    WeaponReceiveCount = data.WeaponCount;
                    WeaponSentCount = LastWeaponSentCount;
                }
            }

            public void UpdateTimeSyncStats(in TimeSyncData data)
            {
                lock (lockObj)
                {
                    Packetloss = data;
                    TimeSync.Update(in data);
                }
            }

            public void UpdateReliableStats(in ReliableLagData data)
            {
                lock (lockObj)
                {
                    ReliableLagData = data;
                }
            }

            public void QueryPositionPing(out PingSummary ping)
            {
                lock (lockObj)
                {
                    ping.Current = PositionPacketPing.Current;
                    ping.Average = PositionPacketPing.Average;
                    ping.Min = PositionPacketPing.Min;
                    ping.Max = PositionPacketPing.Max;
                }
            }

            public void QueryClientPing(out ClientPingSummary ping)
            {
                lock (lockObj)
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
                lock (lockObj)
                {
                    ping.Current = ReliablePing.Current;
                    ping.Average = ReliablePing.Average;
                    ping.Min = ReliablePing.Min;
                    ping.Max = ReliablePing.Max;
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary)
            {
                lock (lockObj)
                {
                    uint s, r;

                    s = Packetloss.ServerPacketsSent;
                    r = Packetloss.ClientPacketsReceived;
                    summary.s2c = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;

                    s = Packetloss.ClientPacketsSent;
                    r = Packetloss.ServerPacketsReceived;
                    summary.c2s = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;

                    s = WeaponSentCount;
                    r = WeaponReceiveCount;
                    summary.s2cwpn = s > PacketlossMinPackets ? (double)(s - r) / s : 0.0;
                }
            }

            public void QueryReliableLag(out ReliableLagData data)
            {
                lock (lockObj)
                {
                    data = ReliableLagData;
                }
            }

            public void QueryTimeSyncHistory(in ICollection<(uint ServerTime, uint ClientTime)> history)
            {
                lock (lockObj)
                {
                    TimeSync.GetHistory(in history);
                }
            }

            public int QueryTimeSyncDrift()
            {
                lock (lockObj)
                {
                    return TimeSync.Drift;
                }
            }
        }

        #endregion
    }
}
