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
        private const int MaxPing = 10000;
        private const int PacketlossMinPackets = 200;
        private const int BucketCount = 25;
        private const int BucketWidth = 20;
        private const int TimeSyncSamples = 24; // timesyncs are usually 5 seconds apart, so this is 2 minutes worth (assuming no packet loss)
        private const int MinSendRoutePercent = 1;
        private const int MaxSendRoutePercent = 999;

        // Required dependencies
        private readonly IComponentBroker _broker;
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

        public LagData(IComponentBroker broker, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
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
                        C2SLatencyEstimateChangedCallback.Fire(_broker, player, updatedC2SLatencyEstimate.Value);
                    }
                }
            }
        }

        #region ILagCollect Members

        void ILagCollect.Position(Player player, int c2sLatency, ushort? clientS2CLatency)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdatePositionStats(c2sLatency, clientS2CLatency);
        }

        void ILagCollect.IncrementWeaponSentCount(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.IncrementWeaponSentCount();
        }

        void ILagCollect.AddWeaponSentCount(Player player, uint value)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.AddWeaponSentCount(value);
        }

        void ILagCollect.SetPendingWeaponSentCount(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.SetPendingWeaponSentCount();
        }

        void ILagCollect.RelDelay(Player player, int ms)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdateReliableAckStats(ms);
        }

        void ILagCollect.ClientLatency(Player player, ref readonly ClientLatencyData data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdateClientLatencyStats(in data);

            PlayerLatencyStatsUpdatedCallback.Fire(_broker, player);
        }

        void ILagCollect.TimeSyncC2SRequestAndS2CRequest(Player player, ref readonly TimeSyncRequestData data, bool requestSent)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdateTimeSyncRequestReceivedStats(in data, requestSent);
        }

        void ILagCollect.TimeSyncS2CRequest(Player player, uint serverRequestTime, uint? clientResponseTime)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdateTimeSyncRequestSentStats(serverRequestTime, clientResponseTime);
        }

        void ILagCollect.TimeSyncC2SResponse(Player player, uint serverRequestTime, uint serverResponseTime, uint clientResponseTime, TimeSpan timestampRTT)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            uint? updatedC2SLatencyEstimate = lagStats.UpdateTimeSyncResponseStats(serverRequestTime, serverResponseTime, clientResponseTime, timestampRTT, GetSendRoutePercent(player));

            if (updatedC2SLatencyEstimate is not null)
            {
                C2SLatencyEstimateChangedCallback.Fire(player.Arena ?? _broker, player, updatedC2SLatencyEstimate.Value);
            }
        }

        void ILagCollect.SetFakeC2SMinLatencyEstimate(Player player, uint estimate)
        {
            if (player.Type != ClientType.Fake)
                return;

            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.SetFakeC2SMinLatencyEstimate(estimate);
        }

        void ILagCollect.RelStats(Player player, ref readonly ReliableLagData data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.UpdateReliableStats(in data);
        }

        void ILagCollect.Clear(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lagStats.Reset();
        }

        #endregion

        #region ILagQuery Members

        void ILagQuery.QueryPositionPing(Player player, out PingSummary ping)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                ping = default;
                return;
            }

            lagStats.QueryPositionPing(out ping);
        }

        void ILagQuery.QueryClientPing(Player player, out PingSummary ping)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                ping = default;
                return;
            }

            lagStats.QueryClientPing(out ping);
        }

        void ILagQuery.QueryClientLagStats(Player player, out ClientLagStats stats)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                stats = default;
                return;
            }

            lagStats.QueryClientLagStats(out stats);
        }

        void ILagQuery.QueryReliablePing(Player player, out PingSummary ping)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                ping = default;
                return;
            }

            lagStats.QueryReliablePing(out ping);
        }

        void ILagQuery.QueryTimeSyncPing(Player player, out PingSummary clientPing, out PingSummary serverPing)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                clientPing = default;
                serverPing = default;
                return;
            }

            lagStats.QueryTimeSyncPing(out clientPing, out serverPing);
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary summary)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                summary = default;
                return;
            }

            lagStats.QueryPacketloss(out summary);
        }

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary summary, out PacketlossDetails details)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                summary = default;
                details = default;
                return;
            }

            lagStats.QueryPacketloss(out summary, out details);
        }

        void ILagQuery.QueryReliableLag(Player player, out ReliableLagData data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                data = default;
                return;
            }

            lagStats.QueryReliableLag(out data);
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
            if (player is null || records is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                records?.Clear();
                return;
            }
            
            lagStats.QueryTimeSyncHistory(records);
        }

        void ILagQuery.QueryTimeSyncDriftTicks(Player player, out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                clientDrift = null;
                serverDriftAvg = null;
                serverDriftStdDev = null;
                return;
            }

            lagStats.QueryTimeSyncDriftTicks(out clientDrift, out serverDriftAvg, out serverDriftStdDev);
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

        int ILagQuery.GetC2SPositionHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetC2SPositionHistogram(data);
        }

        int ILagQuery.GetS2CPositionHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetS2CPositionHistogram(data);
        }


        int ILagQuery.GetReliablePingHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetReliablePingHistogram(data);
        }

        int ILagQuery.GetClientTimeSyncHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetClientTimeSyncHistogram(data);
        }

        int ILagQuery.GetServerTimeSyncHistogram(Player player, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetServerTimeSyncHistogram(data);
        }

        #endregion

        private int GetSendRoutePercent(Player player)
        {
            return player.Arena is not null && _clientSettings is not null
                ? int.Clamp(_clientSettings.GetSetting(player, _sendRoutePercentClientSettingIdentifier), MinSendRoutePercent, MaxSendRoutePercent)
                : 500;
        }

        #region Helper classes

        private class Histogram
        {
            private readonly int _bucketWidth;
            private readonly int _bucketCount;
            private readonly int _minValue;
            private readonly int _maxValue;
            private readonly int[] _buckets;

            /// <summary>
            /// Gets the total number of values added to the histogram.
            /// </summary>
            public int TotalCount { get; private set; }

            /// <summary>
            /// Initializes a new instance of the Histogram class.
            /// </summary>
            /// <param name="bucketWidth">The span of values each bucket contains (must be >= 1).</param>
            /// <param name="bucketCount">The total number of buckets (must be >= 1).</param>
            /// <param name="minValue">The minimum value of the first bucket.</param>
            public Histogram(int bucketWidth, int bucketCount, int minValue)
            {
                if (bucketWidth <= 0)
                    throw new ArgumentOutOfRangeException(nameof(bucketWidth), "Bucket width must be greater than zero.");
                if (bucketCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be greater than zero.");

                _bucketWidth = bucketWidth;
                _bucketCount = bucketCount;
                _minValue = minValue;

                // Calculate the maximum exclusive boundary supported by the buckets
                _maxValue = minValue + (bucketWidth * bucketCount) - 1;
                _buckets = new int[bucketCount];
            }

            /// <summary>
            /// Adds a value to the histogram, clamping it to the bucket range if it falls outside.
            /// </summary>
            /// <param name="value">The integer value to add.</param>
            public void AddValue(int value)
            {
                // Clamp the value to the supported range [_minValue, _maxValue]
                int clampedValue = Math.Clamp(value, _minValue, _maxValue);

                // Determine the target bucket index
                int bucketIndex = (clampedValue - _minValue) / _bucketWidth;

                // Validate the index (though mathematically guaranteed by the clamping logic)
                if (bucketIndex >= 0 && bucketIndex < _bucketCount)
                {
                    _buckets[bucketIndex]++;
                    TotalCount++;
                }
            }

            /// <summary>
            /// Gets a bucket's range by <paramref name="index"/> (e.g., "[10, 20]").
            /// </summary>
            public (int Start, int End) GetBucketRange(int index)
            {
                if (index < 0 || index >= _bucketCount)
                    throw new ArgumentOutOfRangeException(nameof(index), "Bucket index is out of bounds.");

                int start = _minValue + (index * _bucketWidth);
                int end = start + _bucketWidth - 1;
                return (start, end);
            }

            /// <summary>
            /// Gets a copy of the histogram data.
            /// </summary>
            /// <param name="data">A collection to fill with histogram data.</param>
            /// <returns>The total number of data points.</returns>
            public int GetData(ICollection<PingHistogramBucket> data)
            {
                if (data is null)
                    return 0;

                // Find the index of the last bucket containing data.
                int endIndex = _buckets.Length - 1;
                do
                {
                    if (_buckets[endIndex] > 0)
                        break;
                }
                while (--endIndex >= 0);

                if (endIndex < 0)
                    return 0;

                // Find the index of the first bucket containing data.
                int i;
                for (i = 0; i <= endIndex; i++)
                {
                    if (_buckets[i] > 0)
                        break;
                }

                // Copy the data
                data.Clear();

                for (; i <= endIndex; i++)
                {
                    (int start, int end) = GetBucketRange(i);

                    data.Add(
                        new PingHistogramBucket()
                        {
                            Start = start,
                            End = end,
                            Count = _buckets[i]
                        });
                }

                return TotalCount;
            }

            public void Reset()
            {
                for (int i = 0; i < _buckets.Length; i++)
                {
                    _buckets[i] = 0;
                }

                TotalCount = 0;
            }
        }

        private class PingStats(int bucketWidth, int bucketCount, int minValue)
        {
            private readonly Histogram Histogram = new(bucketWidth, bucketCount, minValue);

            /// <summary>
            /// Current ping in milliseconds
            /// </summary>
            public int Current;

            /// <summary>
            /// Average ping in milliseconds
            /// </summary>
            public int Average;

            /// <summary>
            /// Minimum ping in milliseconds
            /// </summary>
            public int Min;

            /// <summary>
            /// Maximum ping in milliseconds
            /// </summary>
            public int Max;

            public void AddValue(int ms)
            {
                // Let the histogram do its own clamp based on buckets.
                Histogram.AddValue(ms);

                // Prevent horribly incorrect pings from messing up stats.
                ms = int.Clamp(ms, 0, MaxPing);

                Current = ms;
                Average = (Average * 7 + ms) / 8; // modified moving average

                if (ms < Min)
                    Min = ms;

                if (ms > Max)
                    Max = ms;
            }

            public int GetHistogram(ICollection<PingHistogramBucket> data)
            {
                return Histogram.GetData(data);
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
                Histogram.Reset();
                Current = 0;
                Average = 0;
                Min = 0;
                Max = 0;
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

            public readonly TimeSpan ServerTimestampRTT;

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

            public readonly int ServerRTT;
            public readonly int? ClientRTT;

            public TimeSyncSample(
                ServerTick serverRequestTime,
                ServerTick serverResponseTime,
                TimeSpan serverTimestampRTT,
                ServerTick? clientRequestTime,
                ServerTick clientResponseTime)
            {
                ServerRequestTime = serverRequestTime;
                ServerResponseTime = serverResponseTime;
                ServerTimestampRTT = serverTimestampRTT;
                ClientRequestTime = clientRequestTime;
                ClientResponseTime = clientResponseTime;

                ServerRTT = int.Clamp(serverResponseTime - serverRequestTime, 0, int.MaxValue);
                ClientRTT = (clientRequestTime is not null)
                    ? int.Clamp(clientResponseTime - clientRequestTime.Value, 0, int.MaxValue)
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

            /// <summary>
            /// Estimate of the C2S latency, based on the minimum RTT in the sample data.
            /// </summary>
            public uint? C2SLatencyEstimate { get; private set; }

            public readonly PingStats ClientPing = new(BucketWidth, BucketCount, 0);
            public readonly PingStats ServerPing = new(BucketWidth, BucketCount, 0);

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
                TimeSpan timestampRTT,
                int sendRoutePercent,
                out bool c2sLatencyEstimateUpdated)
            {
                c2sLatencyEstimateUpdated = false;

                if (_requestSentServerTime != serverRequestTime)
                    return;

                C2SResponseCount++;

                // Save the sample
                int sampleIndex = _samplesNext;
                _samples[sampleIndex] = new TimeSyncSample(serverRequestTime, serverResponseTime, timestampRTT, _requestSentClientTime, clientResponseTime);
                _samplesNext = (sampleIndex + 1) % _samples.Length;

                if (_samplesCount < _samples.Length)
                    _samplesCount++;
                else
                    _samplesHead = (_samplesHead + 1) % _samples.Length;

                ref readonly TimeSyncSample newSample = ref _samples[sampleIndex];

                // Collect ping data for the lag histogram of time sync data.
                ServerPing.AddValue((int)newSample.ServerTimestampRTT.TotalMilliseconds);
                if (newSample.ClientRTT is not null)
                    ClientPing.AddValue(newSample.ClientRTT.Value * 10);

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
                            if (_minRoundtripResultIndex is null || _samples[checkIndex].ServerTimestampRTT < _samples[_minRoundtripResultIndex.Value].ServerTimestampRTT)
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

                        if (newSample.ServerTimestampRTT <= currentSample.ServerTimestampRTT)
                        {
                            changed = newSample.ServerTimestampRTT < currentSample.ServerTimestampRTT;
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

            public bool RefreshC2SLatencyEstimate(int sendRoutePercent)
            {
                if (_minRoundtripResultIndex is null)
                    return false;

                uint newEstimate = (uint)(_samples[_minRoundtripResultIndex!.Value].ServerTimestampRTT.TotalMilliseconds / 10 * sendRoutePercent / 1000);
                
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
            private readonly PingStats _c2sLatencyStats = new(BucketWidth, BucketCount, 0);
            private readonly PingStats _s2cLatencyStats = new(BucketWidth, BucketCount, 0);
            private readonly PingStats _reliablePing = new(BucketWidth, BucketCount, 0);
            private ClientLatencyData _clientReportedData;
            private TimeSyncRequestData _packetloss;
            private readonly TimeSyncStats _timeSync = new();
            private ReliableLagData _reliableLagData;

            /// <summary>
            /// The latest # of weapon packets that the server sent to the client since entering an arena.
            /// </summary>
            /// <remarks>Synchronized with <see cref="Interlocked"/> methods.</remarks>
            private uint _lastWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the start of a security check.
            /// </summary>
            private uint _pendingWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the last successful security check.
            /// </summary>
            private uint _weaponSentCount;

            /// <summary>
            /// The # of weapon packets that the client reported it received since entering an arena, as of the last successful security check.
            /// </summary>
            private uint WeaponReceiveCount => _clientReportedData.WeaponCount;

            private readonly Lock _lock = new();

            public void Reset()
            {
                lock (_lock)
                {
                    _c2sLatencyStats.Reset();
                    _s2cLatencyStats.Reset();
                    _reliablePing.Reset();
                    _clientReportedData = default;
                    _packetloss = default;
                    _timeSync.Reset();
                    _reliableLagData = default;
                    Interlocked.Exchange(ref _lastWeaponSentCount, 0);
                    _pendingWeaponSentCount = 0;
                    _weaponSentCount = 0;
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }

            public void UpdatePositionStats(int c2sLatency, ushort? clientS2CLatency)
            {
                lock (_lock)
                {
                    _c2sLatencyStats.AddValue(c2sLatency * 10); // convert ticks to ms

                    if (clientS2CLatency is not null)
                        _s2cLatencyStats.AddValue(clientS2CLatency.Value * 10); // convert ticks to ms
                }
            }

            public void ResetWeaponSentCount()
            {
                Interlocked.Exchange(ref _lastWeaponSentCount, 0);
            }

            public void IncrementWeaponSentCount()
            {
                Interlocked.Increment(ref _lastWeaponSentCount);
            }

            public void AddWeaponSentCount(uint value)
            {
                Interlocked.Add(ref _lastWeaponSentCount, value);
            }

            public void SetPendingWeaponSentCount()
            {
                lock (_lock)
                {
                    _pendingWeaponSentCount = Interlocked.CompareExchange(ref _lastWeaponSentCount, 0, 0);
                }
            }

            public void UpdateReliableAckStats(int ms)
            {
                lock (_lock)
                {
                    _reliablePing.AddValue(ms);
                }
            }

            public void UpdateClientLatencyStats(ref readonly ClientLatencyData data)
            {
                lock (_lock)
                {
                    _clientReportedData = data;
                    _weaponSentCount = _pendingWeaponSentCount;
                }
            }

            public void UpdateTimeSyncRequestReceivedStats(ref readonly TimeSyncRequestData data, bool requestSent)
            {
                lock (_lock)
                {
                    _packetloss = data;
                    _timeSync.UpdateForRequestReceived(data.ServerTime, data.ClientTime, requestSent);
                }
            }

            public void UpdateTimeSyncRequestSentStats(uint serverTime, uint? clientTime)
            {
                lock (_lock)
                {
                    _timeSync.UpdateForRequestSent(serverTime, clientTime);
                }
            }

            public uint? UpdateTimeSyncResponseStats(uint serverRequestTime, uint serverResponseTime, uint clientResponseTime, TimeSpan timestampRTT, int sendRoutePercent)
            {
                lock (_lock)
                {
                    _timeSync.UpdateForResponseReceived(serverRequestTime, serverResponseTime, clientResponseTime, timestampRTT, sendRoutePercent, out bool c2sLatencyEstimateUpdated);
                    return c2sLatencyEstimateUpdated ? _timeSync.C2SLatencyEstimate!.Value : null;
                }
            }

            public uint? RefreshC2SLatencyEstimate(int sendRoutePercent)
            {
                lock (_lock)
                {
                    return _timeSync.RefreshC2SLatencyEstimate(sendRoutePercent) ? _timeSync.C2SLatencyEstimate!.Value : null;
                }
            }

            public void SetFakeC2SMinLatencyEstimate(uint estimate)
            {
                lock (_lock)
                {
                    _timeSync.OverrideC2SLatencyEstimate(estimate);
                }
            }

            public uint? GetC2SMinLatencyEstimate()
            {
                lock (_lock)
                {
                    return _timeSync.C2SLatencyEstimate;
                }
            }

            public void UpdateReliableStats(ref readonly ReliableLagData data)
            {
                lock (_lock)
                {
                    _reliableLagData = data;
                }
            }

            public void QueryPositionPing(out PingSummary ping)
            {
                lock (_lock)
                {
                    _c2sLatencyStats.GetSummary(out ping);
                }
            }

            public void QueryClientPing(out PingSummary ping)
            {
                lock (_lock)
                {
                    // Client reported latency is in ticks (centiseconds).  Convert to milliseconds.
                    ping.Current = _clientReportedData.LastPing * 10;
                    ping.Average = _clientReportedData.AveragePing * 10;
                    ping.Min = _clientReportedData.LowestPing * 10;
                    ping.Max = _clientReportedData.HighestPing * 10;
                }
            }

            public void QueryClientLagStats(out ClientLagStats stats)
            {
                lock (_lock)
                {
                    // Client reported latency is in ticks (centiseconds).  Convert to milliseconds.
                    stats.S2CAverageCurrent = _clientReportedData.S2CAverageCurrent * 10;
                    stats.S2CSlowTotal = _clientReportedData.S2CSlowTotal;
                    stats.S2CFastTotal = _clientReportedData.S2CFastTotal;
                    stats.S2CSlowCurrent = _clientReportedData.S2CSlowCurrent;
                    stats.S2CFastCurrent = _clientReportedData.S2CFastCurrent;
                }
            }

            public void QueryReliablePing(out PingSummary ping)
            {
                lock (_lock)
                {
                    _reliablePing.GetSummary(out ping);
                }
            }

            public void QueryTimeSyncPing(out PingSummary clientPing, out PingSummary serverPing)
            {
                lock (_lock)
                {
                    _timeSync.ClientPing.GetSummary(out clientPing);
                    _timeSync.ServerPing.GetSummary(out serverPing);
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary)
            {
                lock (_lock)
                {
                    summary.S2C = CalculatePacketloss(_packetloss.ServerPacketsSent, _packetloss.ClientPacketsReceived);
                    summary.C2S = CalculatePacketloss(_packetloss.ClientPacketsSent, _packetloss.ServerPacketsReceived);
                    summary.S2CWeapon = CalculatePacketloss(_weaponSentCount, WeaponReceiveCount);
                    summary.TimeSync = CalculatePacketloss(_timeSync.S2CRequestCount, _timeSync.C2SResponseCount);
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

                    details.ServerPacketsSent = _packetloss.ServerPacketsSent;
                    details.ClientPacketsReceived = _packetloss.ClientPacketsReceived;
                    details.ClientPacketsSent = _packetloss.ClientPacketsSent;
                    details.ServerPacketsReceived = _packetloss.ServerPacketsReceived;
                    details.WeaponSentCount = _weaponSentCount;
                    details.WeaponReceiveCount = WeaponReceiveCount;
                }
            }

            public void QueryReliableLag(out ReliableLagData data)
            {
                lock (_lock)
                {
                    data = _reliableLagData;
                }
            }

            public void QueryTimeSyncHistory(ICollection<TimeSyncRecord> records)
            {
                lock (_lock)
                {
                    _timeSync.GetHistory(records);
                }
            }

            public void QueryTimeSyncDriftTicks(out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
            {
                lock (_lock)
                {
                    // Client value is already in ticks.
                    clientDrift = _clientReportedData.TimerDrift;

                    // Server values are in milliseconds, convert to ticks.
                    serverDriftAvg = _timeSync.DriftAvg / 10;
                    serverDriftStdDev = _timeSync.DriftStdDev / 10;
                }
            }

            public void QueryTimeSyncDriftMs(out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
            {
                lock (_lock)
                {
                    // Client value is in ticks, convert to milliseconds.
                    clientDrift = _clientReportedData.TimerDrift * 10;

                    // Server values are already in milliseconds.
                    serverDriftAvg = _timeSync.DriftAvg;
                    serverDriftStdDev = _timeSync.DriftStdDev;
                }
            }

            public int GetC2SPositionHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return _c2sLatencyStats.GetHistogram(data);
                }
            }

            public int GetS2CPositionHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return _s2cLatencyStats.GetHistogram(data);
                }
            }

            public int GetReliablePingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return _reliablePing.GetHistogram(data);
                }
            }

            public int GetClientTimeSyncHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return _timeSync.ClientPing.GetHistogram(data);
                }
            }

            public int GetServerTimeSyncHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (_lock)
                {
                    return _timeSync.ServerPing.GetHistogram(data);
                }
            }
        }

        #endregion
    }
}
