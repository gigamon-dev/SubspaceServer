using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that tracks lag statistics of players.
    /// </summary>
    [CoreModuleInfo]
    public sealed class LagData : IModule, IModuleLoaderAware, ILagCollect, ILagQuery
    {
        // TODO: maybe some of these could be config settings?
        private const int MaxPing = 10000;
        private const int PacketlossMinPackets = 200;
        private const int MaxBucket = 25;
        private const int BucketWidth = 20;
        private const int TimeSyncSamples = 24; // timesyncs are usually 5 seconds apart, so this is 2 minutes worth (assuming no packet loss)

        // Required dependencies
        private readonly IComponentBroker _broker;
        private readonly ILogManager _logManager;
        private readonly IPlayerData _playerData;

        // Optional dependencies
        private IClientSettings? _clientSettings;

        // Registrations
        private InterfaceRegistrationToken<ILagCollect>? _iLagCollectToken;
        private InterfaceRegistrationToken<ILagQuery>? _iLagQueryToken;

        /// <summary>
        /// per player data key
        /// </summary>
        private PlayerDataKey<PlayerLagStats> _lagkey;

        private ClientSettingIdentifier _sendRoutePercentClientSettingIdentifier; // Latency:SendRoutePercent

        public LagData(IComponentBroker broker, ILogManager logManager, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            
            _lagkey = _playerData.AllocatePlayerData<PlayerLagStats>();

            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _iLagCollectToken = _broker.RegisterInterface<ILagCollect>(this);
            _iLagQueryToken = _broker.RegisterInterface<ILagQuery>(this);

            return true;
        }

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            _clientSettings = broker.GetInterface<IClientSettings>();
            _clientSettings?.TryGetSettingsIdentifier("Latency", "SendRoutePercent", out _sendRoutePercentClientSettingIdentifier);
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            if (_clientSettings is not null)
            {
                broker.ReleaseInterface(ref _clientSettings);
            }
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLagCollectToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iLagQueryToken) != 0)
                return false;

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            _playerData.FreePlayerData(ref _lagkey);

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                {
                    lagStats.ResetWeaponSentCount();

                    // Changing arenas means the Latency:SendRoutePercent setting could have changed.
                    // Refresh the C2S latency estimate.
                    uint? updatedC2SLatencyEstimate = lagStats.RefreshC2SLatencyEstimate(GetSendRoutePercent(player));
                    if (updatedC2SLatencyEstimate is not null)
                    {
                        _logManager.LogP(LogLevel.Drivel, nameof(LagData), player, $"Estimated C2S min latency updated to {updatedC2SLatencyEstimate.Value}.");
                        C2SLatencyEstimateChangedCallback.Fire(_broker, player, updatedC2SLatencyEstimate.Value);
                    }
                }
            }
        }

        #region ILagCollect Members

        void ILagCollect.Position(Player player, int ms, int? clientS2CPing)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdatePositionStats(ms, clientS2CPing);
        }

        void ILagCollect.IncrementWeaponSentCount(Player player)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.IncrementWeaponSentCount();
        }

        void ILagCollect.AddWeaponSentCount(Player player, uint value)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.AddWeaponSentCount(value);
        }

        void ILagCollect.SetPendingWeaponSentCount(Player player)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.SetPendingWeaponSentCount();
        }

        void ILagCollect.RelDelay(Player player, int ms)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateReliableAckStats(ms);
        }

        void ILagCollect.ClientLatency(Player player, ref readonly ClientLatencyData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.UpdateClientLatencyStats(in data);

                PlayerLatencyStatsUpdatedCallback.Fire(_broker, player);
            }
        }

        void ILagCollect.TimeSyncC2SRequestAndS2CRequest(Player player, ref readonly TimeSyncRequestData data, bool requestSent)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateTimeSyncRequestReceivedStats(in data, requestSent);
        }

        void ILagCollect.TimeSyncS2CRequest(Player player, uint serverRequestTime, uint? clientResponseTime)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.UpdateTimeSyncRequestSentStats(serverRequestTime, clientResponseTime);
        }

        void ILagCollect.TimeSyncC2SResponse(Player player, uint serverRequestTime, uint serverResponseTime, uint clientResponseTime)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                uint? updatedC2SLatencyEstimate = lagStats.UpdateTimeSyncResponseStats(serverRequestTime, serverResponseTime, clientResponseTime, GetSendRoutePercent(player));
                if (updatedC2SLatencyEstimate is not null)
                {
                    _logManager.LogP(LogLevel.Drivel, nameof(LagData), player, $"Estimated C2S min latency updated to {updatedC2SLatencyEstimate.Value}.");
                    C2SLatencyEstimateChangedCallback.Fire(player.Arena ?? _broker, player, updatedC2SLatencyEstimate.Value);
                }
            }
        }

        void ILagCollect.SetFakeC2SMinLatencyEstimate(Player player, uint estimate)
        {
            if (player.Type != ClientType.Fake)
                return;

            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.SetFakeC2SMinLatencyEstimate(estimate);
            }
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

        void ILagQuery.QueryTimeSyncPing(Player player, out PingSummary clientPing, out PingSummary serverPing)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.QueryTimeSyncPing(out clientPing, out serverPing);
            }
            else
            {
                clientPing = default;
                serverPing = default;
            }
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary packetloss)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryPacketloss(out packetloss);
            else
                packetloss = default;
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary summary, out PacketlossDetails details)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.QueryPacketloss(out summary, out details);
            }
            else
            {
                summary = default;
                details = default;
            }
        }

        void ILagQuery.QueryReliableLag(Player player, out ReliableLagData data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryReliableLag(out data);
            else
                data = default;
        }

        bool ILagQuery.TryGetC2SMinLatencyEstimate(Player player, out uint estimate)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                uint? c2s = lagStats.GetC2SMinLatencyEstimate();
                if (c2s is not null)
                {
                    estimate = c2s.Value;
                    return true;
                }
            }

            estimate = default;
            return false;
        }

        void ILagQuery.QueryTimeSyncHistory(Player player, ICollection<TimeSyncRecord> records)
        {
            if (player is not null && records is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                lagStats.QueryTimeSyncHistory(records);
            else
                records?.Clear();
        }

        void ILagQuery.QueryTimeSyncDriftTicks(Player player, out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.QueryTimeSyncDriftTicks(out clientDrift, out serverDriftAvg, out serverDriftStdDev);
                
            }
            else
            {
                clientDrift = null;
                serverDriftAvg = null;
                serverDriftStdDev = null;
            }
        }

        void ILagQuery.QueryTimeSyncDriftMs(Player player, out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                lagStats.QueryTimeSyncDriftMs(out clientDrift, out serverDriftAvg, out serverDriftStdDev);
            }
            else
            {
                clientDrift = null;
                serverDriftAvg = null;
                serverDriftStdDev = null;
            }
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

        bool ILagQuery.GetTimeSyncClientPingHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return lagStats.GetTimeSyncClientPingHistogram(data);
            else
                return false;
        }

        bool ILagQuery.GetTimeSyncServerPingHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is not null && player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return lagStats.GetTimeSyncServerPingHistogram(data);
            else
                return false;
        }


        #endregion

        private uint GetSendRoutePercent(Player player)
        {
            return player.Arena is not null && _clientSettings is not null
                ? (uint)int.Clamp(_clientSettings.GetSetting(player, _sendRoutePercentClientSettingIdentifier), 100, 900)
                : 500;
        }

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

            public void GetSummary(out PingSummary summary)
            {
                summary.Current = Current;
                summary.Average = Average;
                summary.Min = Min;
                summary.Max = Max;
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

        /// <summary>
        /// A single S2C time sync sample.
        /// </summary>
        private readonly struct TimeSyncSample
        { 
            /// <summary>
            /// The server time that the S2C request was sent.
            /// </summary>
            public readonly ServerTick ServerRequestTime;

            /// <summary>
            /// The server time that the C2S response was received.
            /// </summary>
            public readonly ServerTick ServerResponseTime;

            /// <summary>
            /// The client time received when the S2C request was sent.
            /// Only if the server initiated the S2C request when it received an incoming C2S request, otherwise <see langword="null"/>.
            /// The client's time when it sent the C2S request.
            /// </summary>
            public readonly ServerTick? ClientRequestTime;

            /// <summary>
            /// The client time from the C2S response. The client's time when it received the S2C request.
            /// </summary>
            public readonly ServerTick ClientResponseTime;

            public readonly uint ServerRTT;
            public readonly uint? ClientRTT;

            public TimeSyncSample(
                ServerTick serverRequestTime,
                ServerTick serverResponseTime,
                ServerTick? clientRequestTime,
                ServerTick clientResponseTime)
            {
                ServerRequestTime = serverRequestTime;
                ServerResponseTime = serverResponseTime;
                ClientRequestTime = clientRequestTime;
                ClientResponseTime = clientResponseTime;

                ServerRTT = (uint)(serverResponseTime - serverRequestTime);
                ClientRTT = (clientRequestTime is not null)
                    ? (uint)(clientResponseTime - clientRequestTime)
                    : null;
            }
        }

        private class TimeSyncStats
        {
            //
            // Data of incoming requests (client initiated, C2S requests received)
            //

            private readonly TimeSyncRecord[] _records = new TimeSyncRecord[TimeSyncSamples];
            private int _next = 0;
            private int _count = 0;
            private int? _driftAvg = null;
            private double? _driftStdDev = null;
            private bool _driftIsDirty = false;

            //
            // Data of outgoing requests (server initiated, S2C requests sent with C2S responses received)
            //

            /// <summary>
            /// The # of timesync requests sent by the server.
            /// </summary>
            public uint S2CRequestCount { get; private set; }

            /// <summary>
            /// The # of timesync responses received from the client.
            /// </summary>
            public uint C2SResponseCount { get; private set; }

            /// <summary>
            /// The server time that a time sync request was last sent. When we get a response, it should match.
            /// </summary>
            private uint? _requestSentServerTime;

            /// <summary>
            /// The client time that was received right before a time sync request was last sent.
            /// This can be <see langword="null"/> if the outgoing time sync request was sent without first receiving a client time.
            /// This value allows us to calculate an additional RTT when the response is received.
            /// </summary>
            private uint? _requestSentClientTime;

            private readonly TimeSyncSample[] _samples = new TimeSyncSample[TimeSyncSamples];
            private int _samplesHead = 0;
            private int _samplesNext = 0;
            private int _samplesCount = 0;

            private int? _minRoundtripResultIndex;
            public uint? C2SLatencyEstimate { get; private set; }

            public readonly PingStats ClientPing = new();
            public readonly PingStats ServerPing = new();

            public void UpdateForRequestReceived(uint serverTime, uint clientTime, bool requestSent)
            {
                int sampleIndex = _next;
                _records[sampleIndex].ServerTime = serverTime;
                _records[sampleIndex].ClientTime = clientTime;
                _next = (sampleIndex + 1) % _records.Length;
                _driftIsDirty = true; // drift is calculated lazily when accessed

                if (_count < _records.Length)
                    _count++;

                if (requestSent)
                {
                    UpdateForRequestSent(serverTime, clientTime);
                }
            }

            public void UpdateForRequestSent(uint serverTime, uint? clientTime)
            {
                _requestSentServerTime = serverTime;
                _requestSentClientTime = clientTime;
                S2CRequestCount++;
            }

            public void UpdateForResponseReceived(
                uint serverRequestTime,
                uint serverResponseTime,
                uint clientResponseTime,
                uint sendRoutePercent,
                out bool c2sLatencyEstimateUpdated)
            {
                c2sLatencyEstimateUpdated = false;

                if (_requestSentServerTime != serverRequestTime)
                    return;

                C2SResponseCount++;

                // Save the sample
                int sampleIndex = _samplesNext;
                _samples[sampleIndex] = new TimeSyncSample(serverRequestTime, serverResponseTime, _requestSentClientTime, clientResponseTime);
                _samplesNext = (sampleIndex + 1) % _samples.Length;

                if (_samplesCount < _samples.Length)
                    _samplesCount++;
                else
                    _samplesHead = (_samplesHead + 1) % _samples.Length;

                ref readonly TimeSyncSample newSample = ref _samples[sampleIndex];

                // Collect ping data for the lag histogram of time sync data.
                ServerPing.Add((int)newSample.ServerRTT * 10);
                if (newSample.ClientRTT is not null)
                    ClientPing.Add((int)newSample.ClientRTT.Value * 10);

                // Track min C2S RTT over a set of the most recent samples.
                bool changed = false;
                if (_minRoundtripResultIndex is not null)
                {
                    if (_minRoundtripResultIndex.Value == sampleIndex)
                    {
                        // The minimum RTT record has been overwritten.
                        // Recalculate it by going through all of the data.
                        // Start with the most recent and look for a shorter roundtrip time.
                        _minRoundtripResultIndex = null;
                        for (int i = _samplesCount - 1; i >= 0; i--)
                        {
                            int checkIndex = (_samplesHead + i) % _samples.Length;
                            if (_minRoundtripResultIndex is null || _samples[checkIndex].ServerRTT < _samples[_minRoundtripResultIndex.Value].ServerRTT)
                            {
                                _minRoundtripResultIndex = checkIndex;
                                changed = true;
                            }
                        }
                    }
                    else
                    {
                        // Compare the new sample with the current known minimum.
                        ref readonly TimeSyncSample currentSample = ref _samples[_minRoundtripResultIndex.Value];

                        if (newSample.ServerRTT <= currentSample.ServerRTT)
                        {
                            changed = newSample.ServerRTT < currentSample.ServerRTT;
                            _minRoundtripResultIndex = sampleIndex;
                        }
                    }
                }
                else
                {
                    _minRoundtripResultIndex = sampleIndex;
                    changed = true;
                }

                if (changed)
                {
                    // The RTT changed. Calculate a new C2S latency estimate.
                    // Try to refresh the active C2S latency estimate.
                    c2sLatencyEstimateUpdated = RefreshC2SLatencyEstimate(sendRoutePercent);
                }
            }

            public bool RefreshC2SLatencyEstimate(uint sendRoutePercent)
            {
                if (_minRoundtripResultIndex is null)
                    return false;

                uint newEstimate = _samples[_minRoundtripResultIndex!.Value].ServerRTT * sendRoutePercent / 1000;
                
                if (C2SLatencyEstimate is null // no active estimate yet
                    || C2SLatencyEstimate.Value != newEstimate) // estimate is dirty
                {
                    C2SLatencyEstimate = newEstimate;
                    return true;
                }

                return false;
            }

            public void OverrideC2SLatencyEstimate(uint estimate)
            {
                C2SLatencyEstimate = estimate;
            }

            /// <summary>
            /// Average drift (milliseconds)
            /// </summary>
            public int? DriftAvg
            {
                get
                {
                    RefreshDrift();
                    return _driftAvg;
                }
            }

            /// <summary>
            /// Standard deviation of drift.
            /// </summary>
            public double? DriftStdDev
            {
                get
                {
                    RefreshDrift();
                    return _driftStdDev;
                }
            }

            private void RefreshDrift()
            {
                if (!_driftIsDirty)
                {
                    // Already up to date.
                    return;
                }

                _driftIsDirty = false;

                if (_count < 2)
                {
                    // Need at least 2 time sync samples to calculate drift.
                    return;
                }

                Span<int> driftValues = stackalloc int[_count - 1];
                int total = 0;
                int count = 0;

                for (int i = _count; i > 1; i--)
                {
                    int j = (_next + _records.Length - i) % _records.Length;
                    int k = (_next + _records.Length - (i - 1)) % _records.Length;

                    int delta = (new ServerTick(_records[j].ServerTime) - new ServerTick(_records[j].ClientTime))
                        - (new ServerTick(_records[k].ServerTime) - new ServerTick(_records[k].ClientTime));

                    if (delta >= -10000 && delta <= 10000)
                    {
                        // Convert from ticks (centiseconds) to milliseconds
                        delta *= 10;

                        driftValues[count] = delta;
                        total += delta;
                        count++;
                    }
                }

                _driftAvg = count > 0 ? total / count : null;

                // Need at least 2 drift samples to calculate standard deviation.
                if (count >= 2)
                {
                    driftValues = driftValues[..count];

                    double variance = 0;
                    foreach (int val in driftValues)
                    {
                        int difference = val - _driftAvg!.Value;
                        variance += (difference * difference);
                    }
                    variance /= count;
                    _driftStdDev = Math.Sqrt(variance);
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

            public void Reset()
            {
                // client initiated data
                Array.Clear(_records);
                _next = 0;
                _count = 0;
                _driftAvg = null;
                _driftStdDev = null;
                _driftIsDirty = false;

                // server initiated data
                S2CRequestCount = 0;
                C2SResponseCount = 0;
                _requestSentServerTime = null;
                _requestSentClientTime = null;
                Array.Clear(_samples);
                _samplesHead = 0;
                _samplesNext = 0;
                _samplesCount = 0;
                _minRoundtripResultIndex = null;
                C2SLatencyEstimate = null;
                ClientPing.Reset();
                ServerPing.Reset();
            }
        }

        private class PlayerLagStats : IResettable
        {
            private readonly PingStats PositionPacketPing = new();
            private readonly PingStats ReliablePing = new();
            private ClientLatencyData ClientReportedData;
            private TimeSyncRequestData Packetloss;
            private readonly TimeSyncStats TimeSync = new();
            private ReliableLagData ReliableLagData;

            /// <summary>
            /// The latest # of weapon packets that the server sent to the client since entering an arena.
            /// </summary>
            /// <remarks>Synchronized with <see cref="Interlocked"/> methods.</remarks>
            private uint LastWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the start of a security check.
            /// </summary>
            private uint PendingWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the last successful security check.
            /// </summary>
            private uint WeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the client reported it received since entering an arena, as of the last successful security check.
            /// </summary>
            private uint WeaponReceiveCount => ClientReportedData.WeaponCount;

            private readonly Lock _lock = new();

            public void Reset()
            {
                lock (_lock)
                {
                    PositionPacketPing.Reset();
                    ReliablePing.Reset();
                    ClientReportedData = default;
                    Packetloss = default;
                    TimeSync.Reset();
                    ReliableLagData = default;
                    Interlocked.Exchange(ref LastWeaponSentCount, 0);
                    PendingWeaponSentCount = 0;
                    WeaponSentCount = 0;
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }

            public void UpdatePositionStats(int ms, int? clientS2CPing)
            {
                lock (_lock)
                {
                    PositionPacketPing.Add(ms * 2); // convert one-way to round-trip

                    // TODO: do something with clientS2CPing?
                }
            }

            public void ResetWeaponSentCount()
            {
                Interlocked.Exchange(ref LastWeaponSentCount, 0);
            }

            public void IncrementWeaponSentCount()
            {
                Interlocked.Increment(ref LastWeaponSentCount);
            }

            public void AddWeaponSentCount(uint value)
            {
                Interlocked.Add(ref LastWeaponSentCount, value);
            }

            public void SetPendingWeaponSentCount()
            {
                lock (_lock)
                {
                    PendingWeaponSentCount = Interlocked.CompareExchange(ref LastWeaponSentCount, 0, 0);
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
                    ClientReportedData = data;
                    WeaponSentCount = PendingWeaponSentCount;
                }
            }

            public void UpdateTimeSyncRequestReceivedStats(ref readonly TimeSyncRequestData data, bool requestSent)
            {
                lock (_lock)
                {
                    Packetloss = data;
                    TimeSync.UpdateForRequestReceived(data.ServerTime, data.ClientTime, requestSent);
                }
            }

            public void UpdateTimeSyncRequestSentStats(uint serverTime, uint? clientTime)
            {
                lock (_lock)
                {
                    TimeSync.UpdateForRequestSent(serverTime, clientTime);
                }
            }

            public uint? UpdateTimeSyncResponseStats(uint serverRequestTime, uint serverResponseTime, uint clientResponseTime, uint sendRoutePercent)
            {
                lock (_lock)
                {
                    TimeSync.UpdateForResponseReceived(serverRequestTime, serverResponseTime, clientResponseTime, sendRoutePercent, out bool c2sLatencyEstimateUpdated);
                    return c2sLatencyEstimateUpdated ? TimeSync.C2SLatencyEstimate!.Value : null;
                }
            }

            public uint? RefreshC2SLatencyEstimate(uint sendRoutePercent)
            {
                lock (_lock)
                {
                    return TimeSync.RefreshC2SLatencyEstimate(sendRoutePercent) ? TimeSync.C2SLatencyEstimate!.Value : null;
                }
            }

            public void SetFakeC2SMinLatencyEstimate(uint estimate)
            {
                lock (_lock)
                {
                    TimeSync.OverrideC2SLatencyEstimate(estimate);
                }
            }

            public uint? GetC2SMinLatencyEstimate()
            {
                lock(_lock)
                {
                    return TimeSync.C2SLatencyEstimate;
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
                    PositionPacketPing.GetSummary(out ping);
                }
            }

            public void QueryClientPing(out ClientPingSummary ping)
            {
                lock (_lock)
                {
                    // ClientReportedPing is in ticks (centiseconds).  Convert to milliseconds.
                    ping.Current = ClientReportedData.LastPing * 10;
                    ping.Average = ClientReportedData.AveragePing * 10;
                    ping.Min = ClientReportedData.LowestPing * 10;
                    ping.Max = ClientReportedData.HighestPing * 10;
                    ping.S2CAverageCurrent = ClientReportedData.S2CAverageCurrent * 10;
                    ping.S2CSlowTotal = ClientReportedData.S2CSlowTotal;
                    ping.S2CFastTotal = ClientReportedData.S2CFastTotal;
                    ping.S2CSlowCurrent = ClientReportedData.S2CSlowCurrent;
                    ping.S2CFastCurrent = ClientReportedData.S2CFastCurrent;
                }
            }

            public void QueryReliablePing(out PingSummary ping)
            {
                lock (_lock)
                {
                    ReliablePing.GetSummary(out ping);
                }
            }

            public void QueryTimeSyncPing(out PingSummary clientPing, out PingSummary serverPing)
            {
                lock (_lock)
                {
                    TimeSync.ClientPing.GetSummary(out clientPing);
                    TimeSync.ServerPing.GetSummary(out serverPing);
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary)
            {
                lock (_lock)
                {
                    summary.S2C = CalculatePacketloss(Packetloss.ServerPacketsSent, Packetloss.ClientPacketsReceived);
                    summary.C2S = CalculatePacketloss(Packetloss.ClientPacketsSent, Packetloss.ServerPacketsReceived);
                    summary.S2CWeapon = CalculatePacketloss(WeaponSentCount, WeaponReceiveCount);
                    summary.TimeSync = CalculatePacketloss(TimeSync.S2CRequestCount, TimeSync.C2SResponseCount);
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static double CalculatePacketloss(uint sent, uint received)
                {
                    // The difference between sent and received is signed. This allows for negative packetloss.
                    // The rest is unsigned, which allows for use of the full range of 32-bit values.
                    return sent > PacketlossMinPackets ? (double)((int)sent - (int)received) / sent : 0.0;
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary, out PacketlossDetails details)
            {
                lock (_lock)
                {
                    QueryPacketloss(out summary);

                    details.ServerPacketsSent = Packetloss.ServerPacketsSent;
                    details.ClientPacketsReceived = Packetloss.ClientPacketsReceived;
                    details.ClientPacketsSent = Packetloss.ClientPacketsSent;
                    details.ServerPacketsReceived = Packetloss.ServerPacketsReceived;
                    details.WeaponSentCount = WeaponSentCount;
                    details.WeaponReceiveCount = WeaponReceiveCount;
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

            public void QueryTimeSyncDriftTicks(out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
            {
                lock (_lock)
                {
                    // Client value is already in ticks.
                    clientDrift = ClientReportedData.TimerDrift;

                    // Server values are in milliseconds, convert to ticks.
                    serverDriftAvg = TimeSync.DriftAvg / 10;
                    serverDriftStdDev = TimeSync.DriftStdDev / 10;
                }
            }

            public void QueryTimeSyncDriftMs(out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
            {
                lock (_lock)
                {
                    // Client value is in ticks, convert to milliseconds.
                    clientDrift = ClientReportedData.TimerDrift * 10;

                    // Server values are already in milliseconds.
                    serverDriftAvg = TimeSync.DriftAvg;
                    serverDriftStdDev = TimeSync.DriftStdDev;
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

            public bool GetTimeSyncClientPingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return GetPingHistogram(TimeSync.ClientPing, data);
                }
            }

            public bool GetTimeSyncServerPingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return GetPingHistogram(TimeSync.ServerPing, data);
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
