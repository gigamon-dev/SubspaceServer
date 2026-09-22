using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using GlobalConf = SS.Core.ConfigHelp.Constants.Global;

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
        private const int PositionBucketCount = 30;
        private const int TimeSyncSamples = 24; // timesyncs are usually 5 seconds apart, so this is 2 minutes worth (assuming no packet loss)
        private const int MinSendRoutePercent = 1;
        private const int MaxSendRoutePercent = 999;
        private const double C2SEstimateMarginOfError = 0.1; // 0.1 centiseconds = 1 ms

        // Required dependencies
        private readonly IComponentBroker _broker;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IPlayerData _playerData;

        // Optional dependencies
        private IClientSettings? _clientSettings;

        // Registrations
        private InterfaceRegistrationToken<ILagCollect>? _iLagCollectToken;
        private InterfaceRegistrationToken<ILagQuery>? _iLagQueryToken;

        // Global.conf [Latency] settings for dynamic adjustment of Latency:SendRoutePercent
        private bool _dynamicSendRoutePercentEnabled;
        private TimeSpan _dynamicSendRoutePercentAdjustInterval;
        private int _dynamicSendRoutePercentC2SMinSampleSize;
        private int _dynamicSendRoutePercentMinRTTRequired;
        private int _dynamicSendRoutePercentMinimumAdjust;
        private int _dynamicSendRoutePercentMinValue;
        private int _dynamicSendRoutePercentMaxValue;

        /// <summary>
        /// per player data key
        /// </summary>
        private PlayerDataKey<PlayerLagStats> _lagkey;

        private ClientSettingIdentifier _sendRoutePercentClientSettingIdentifier; // Latency:SendRoutePercent

        public LagData(IComponentBroker broker, IConfigManager configManager, ILogManager logManager, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region IModule Members

        [ConfigHelp<bool>("Latency", "DynamicSendRoutePercentEnabled", ConfigScope.Global, Default = false,
            Description = "Whether to dynamically adjust Latency:SendRoutePercent based on lag stats (position packet times and RTT from time syncs).")]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentAdjustInterval", ConfigScope.Global, Default = 180, Min = 0,
            Description = "After adjusting Latency:SendRoutePercent, the amount of time (seconds) until it can be adjusted again.")]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentC2SMinSampleSize", ConfigScope.Global, Default = 1000, Min = 100,
            Description = """
                The minimum # of position packet data points required to make a new estimate to dynamically adjust Latency:SendRoutePercent.
                Note: The system will only make a C2S latency point estimation if the sample size of position packets is large enough to meet
                the required confidence level, according to the variance.
                """)]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentMinRTTRequired", ConfigScope.Global, Default = 2, Min = 2,
            Description = "The minimum round-trip time (ms) required to dynamically adjust Latency:SendRoutePercent")]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentMinimumAdjust", ConfigScope.Global, Default = 10, Min = 1, Max = 1000,
            Description = """
                The minimum amount to adjust Latency:SendRoutePercent when a new estimate is made (enough data points).
                This can be useful to prevent overriding when there's little difference.
                In 0.1% (1000 = 100%, 500 = 50%, 10 = 1%, 1 = 0.1%).
                """)]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentMinValue", ConfigScope.Global, Default = 250, Min = 1, Max = 500,
            Description = "The minimum Latency:SendRoutePercent the system will dynamically adjust to.")]
        [ConfigHelp<int>("Latency", "DynamicSendRoutePercentMaxValue", ConfigScope.Global, Default = 750, Min = 500, Max = 999,
            Description = "The maximum Latency:SendRoutePercent the system will dynamically adjust to.")]
        bool IModule.Load(IComponentBroker broker)
        {
            _dynamicSendRoutePercentEnabled = _configManager.GetBool(_configManager.Global, "Latency", "DynamicSendRoutePercentEnabled", GlobalConf.Latency.DynamicSendRoutePercentEnabled.Default);
            _dynamicSendRoutePercentAdjustInterval = TimeSpan.FromSeconds(int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentAdjustInterval", GlobalConf.Latency.DynamicSendRoutePercentAdjustInterval.Default), GlobalConf.Latency.DynamicSendRoutePercentAdjustInterval.Min, int.MaxValue));
            _dynamicSendRoutePercentC2SMinSampleSize = int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentC2SMinSampleSize", GlobalConf.Latency.DynamicSendRoutePercentC2SMinSampleSize.Default), GlobalConf.Latency.DynamicSendRoutePercentC2SMinSampleSize.Min, int.MaxValue);
            _dynamicSendRoutePercentMinRTTRequired = int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentMinRTTRequired", GlobalConf.Latency.DynamicSendRoutePercentMinRTTRequired.Default), GlobalConf.Latency.DynamicSendRoutePercentMinRTTRequired.Min, int.MaxValue);
            _dynamicSendRoutePercentMinimumAdjust = int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentMinimumAdjust", GlobalConf.Latency.DynamicSendRoutePercentMinimumAdjust.Default), GlobalConf.Latency.DynamicSendRoutePercentMinimumAdjust.Min, GlobalConf.Latency.DynamicSendRoutePercentMinimumAdjust.Max);
            _dynamicSendRoutePercentMinValue = int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentMinValue", GlobalConf.Latency.DynamicSendRoutePercentMinValue.Default), GlobalConf.Latency.DynamicSendRoutePercentMinValue.Min, GlobalConf.Latency.DynamicSendRoutePercentMinValue.Max);
            _dynamicSendRoutePercentMaxValue = int.Clamp(_configManager.GetInt(_configManager.Global, "Latency", "DynamicSendRoutePercentMaxValue", GlobalConf.Latency.DynamicSendRoutePercentMaxValue.Default), GlobalConf.Latency.DynamicSendRoutePercentMaxValue.Min, GlobalConf.Latency.DynamicSendRoutePercentMaxValue.Max);

            _lagkey = _playerData.AllocatePlayerData<PlayerLagStats>();

            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            BeforeSendInitialClientSettingsCallback.Register(_broker, Callback_BeforeSendInitialClientSettings);

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
            BeforeSendInitialClientSettingsCallback.Unregister(_broker, Callback_BeforeSendInitialClientSettings);

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
                    Interlocked.Exchange(ref lagStats.LastWeaponSentCount, 0);

                    int sendRoutePercent = GetSendRoutePercent(player);

                    // Changing arenas means the Latency:SendRoutePercent setting could have changed.
                    // Refresh the C2S latency estimate.
                    uint? updatedC2SLatencyEstimate;
                    lock (lagStats.Lock)
                    {
                        updatedC2SLatencyEstimate = lagStats.TimeSync.RefreshC2SLatencyEstimate(sendRoutePercent) ? lagStats.TimeSync.C2SLatencyEstimate!.Value : null;
                    }

                    if (updatedC2SLatencyEstimate is not null)
                    {
                        C2SLatencyEstimateChangedCallback.Fire(_broker, player, updatedC2SLatencyEstimate.Value);
                    }
                }
            }
        }

        private void Callback_BeforeSendInitialClientSettings(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            if (_dynamicSendRoutePercentEnabled && _clientSettings is not null)
            {
                if (lagStats.SendRoutePercentOverride is not null)
                {
                    // The player switched arenas, re-override Latency:SendRoutePercent.
                    _clientSettings.OverrideSetting(player, _sendRoutePercentClientSettingIdentifier, lagStats.SendRoutePercentOverride.Value);

                    // The client setting packet is sent immediately after this callback is executed,
                    // so there is no need to call _clientSettings.SendClientSettings(player).
                    // Doing so would double send the packet.
                }
            }
        }

        #region ILagCollect Members

        void ILagCollect.Position(Player player, int c2sLatency, ushort? clientS2CLatency)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            int? newSendRoutePercent = null;

            lock (lagStats.Lock)
            {
                lagStats.PositionStats.Add(c2sLatency, clientS2CLatency);

                if (_dynamicSendRoutePercentEnabled 
                    && _clientSettings is not null
                    && (lagStats.SendRoutePercentLastUpdated is null || (lagStats.SendRoutePercentLastUpdated.Value + _dynamicSendRoutePercentAdjustInterval) < DateTime.UtcNow)
                    && lagStats.PositionStats.C2SSampleCount >= _dynamicSendRoutePercentC2SMinSampleSize)
                {
                    /*
                     * Dynamic Latency:SendRoutePercent
                     * --------------------------------
                     * Detect imbalances between c2s latency and s2c latency by looking at the most recent sample set 
                     * containing the differences between server times and position packet times.
                     * Automatically adjust the Latency:SendRoutePercent client setting to nudge the client closer into sync.
                     * 
                     * Let:
                     * rtt = minimum in the recent timesync sample set, converted to centiseconds
                     * minC2S = the minimum latency recorded in recent samples of position packet times. 
                     * 
                     * Calculate what the client should have estimated the c2s latency to be using rtt and the current Latency:SendRoutePercent:
                     * expected = rtt * currentSendRoutePercent / 1000
                     * 
                     * In theory, minC2S should match the expected value if everything was spot on.
                     * Take the difference:
                     * diff = minC2S - expected
                     * 
                     * Adjust Latency:SendRoutePercent if diff != 0:
                     * adjustPercent = 1000 * diff / rtt
                     * newSendRoutePercent = currentSendRoutePercent + adjustPercent
                     */

                    TimeSpan? minRTT = lagStats.TimeSync.GetMinRTT();
                    if (minRTT is null)
                    {
                        // Don't have a RTT yet.
                        return;
                    }

                    try
                    {
                        int rtt = (int)(minRTT.Value.TotalMilliseconds / 10);
                        if (rtt < _dynamicSendRoutePercentMinRTTRequired)
                        {
                            // RTT is below the configured threshold for dynamically adjusting Latency:SendRoutePercent.
                            _logManager.LogP(LogLevel.Drivel, nameof(LagData), player, $"Dynamic SendRoutePercent - RTT too low (rtt: {rtt}, minrtt: {_dynamicSendRoutePercentMinRTTRequired})");
                            return;
                        }

                        int currentSendRoutePercent = GetSendRoutePercent(player);
                        int expected = rtt * currentSendRoutePercent / 1000;
                        int minC2S = lagStats.PositionStats.C2SSampleMinimum;
                        int diff = minC2S - expected;
                        if (diff == 0)
                        {
                            // No adjustment needed.
                            _logManager.LogP(LogLevel.Drivel, nameof(LagData), player, $"Dynamic SendRoutePercent calculated (rtt: {rtt}, c2s: {minC2S}, expected: {expected}, diff: {diff}, adjust: NONE, current: {currentSendRoutePercent}");
                            return;
                        }

                        int adjustPercent = 1000 * diff / rtt;

                        if (int.Abs(adjustPercent) >= _dynamicSendRoutePercentMinimumAdjust)
                        {
                            newSendRoutePercent = int.Clamp(currentSendRoutePercent + adjustPercent, _dynamicSendRoutePercentMinValue, _dynamicSendRoutePercentMaxValue);

                            if (newSendRoutePercent.Value == currentSendRoutePercent)
                            {
                                // No change (clamped to min or max value already)
                                newSendRoutePercent = null;
                            }
                            else
                            {
                                lagStats.SendRoutePercentOverride = newSendRoutePercent;
                                lagStats.SendRoutePercentLastUpdated = DateTime.UtcNow;
                            }
                        }

                        _logManager.LogP(LogLevel.Drivel, nameof(LagData), player, $"Dynamic SendRoutePercent calculated (rtt: {rtt}, c2s: {minC2S}, expected: {expected}, diff: {diff}, adjust: {adjustPercent}, current: {currentSendRoutePercent}, new: {newSendRoutePercent}");
                    }
                    finally
                    {
                        lagStats.PositionStats.ResetSampleStats();
                    }
                }
            }

            // If Latency:SendRoutePercent is to be adjusted, do it outside of the lock.
            if (newSendRoutePercent is not null && _clientSettings is not null)
            {
                _clientSettings.OverrideSetting(player, _sendRoutePercentClientSettingIdentifier, newSendRoutePercent.Value);
                _clientSettings.SendClientSettings(player);
            }
        }

        void ILagCollect.IncrementWeaponSentCount(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            Interlocked.Increment(ref lagStats.LastWeaponSentCount);
        }

        void ILagCollect.AddWeaponSentCount(Player player, uint value)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            Interlocked.Add(ref lagStats.LastWeaponSentCount, value);
        }

        void ILagCollect.SetPendingWeaponSentCount(Player player)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.PendingWeaponSentCount = Interlocked.CompareExchange(ref lagStats.LastWeaponSentCount, 0, 0);
            }
        }

        void ILagCollect.RelDelay(Player player, int ms)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.ReliablePing.AddValue(ms);
            }
        }

        void ILagCollect.ClientLatency(Player player, ref readonly ClientLatencyData data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.ClientReportedData = data;
                lagStats.WeaponSentCount = lagStats.PendingWeaponSentCount;
            }

            PlayerLatencyStatsUpdatedCallback.Fire(_broker, player);
        }

        void ILagCollect.TimeSyncC2SRequestAndS2CRequest(Player player, ref readonly TimeSyncRequestData data, bool requestSent)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.Packetloss = data;
                lagStats.TimeSync.UpdateForRequestReceived(data.ServerTime, data.ClientTime, requestSent);
            }
        }

        void ILagCollect.TimeSyncS2CRequest(Player player, uint serverRequestTime, uint? clientResponseTime)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.TimeSync.UpdateForRequestSent(serverRequestTime, clientResponseTime);
            }
        }

        void ILagCollect.TimeSyncC2SResponse(Player player, uint serverRequestTime, uint serverResponseTime, uint clientResponseTime, TimeSpan timestampRTT)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            int sendRoutePercent = GetSendRoutePercent(player);
            uint? updatedC2SLatencyEstimate;

            lock (lagStats.Lock)
            {
                lagStats.TimeSync.UpdateForResponseReceived(serverRequestTime, serverResponseTime, clientResponseTime, timestampRTT, sendRoutePercent, out bool c2sLatencyEstimateUpdated);
                updatedC2SLatencyEstimate = c2sLatencyEstimateUpdated ? lagStats.TimeSync.C2SLatencyEstimate!.Value : null;
            }

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

            lock (lagStats.Lock)
            {
                lagStats.TimeSync.OverrideC2SLatencyEstimate(estimate);
            }
        }

        void ILagCollect.RelStats(Player player, ref readonly ReliableLagData data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return;

            lock (lagStats.Lock)
            {
                lagStats.ReliableLagData = data;
            }
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

            lock (lagStats.Lock)
            {
                lagStats.PositionStats.GetC2SSummary(out ping);
            }
        }

        void ILagQuery.QueryClientPing(Player player, out ClientPingSummary ping)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                ping = default;
                return;
            }

            lock (lagStats.Lock)
            {
                // ClientReportedPing is in ticks (centiseconds).  Convert to milliseconds.
                ping.Current = lagStats.ClientReportedData.LastPing * 10;
                ping.Average = lagStats.ClientReportedData.AveragePing * 10;
                ping.Min = lagStats.ClientReportedData.LowestPing * 10;
                ping.Max = lagStats.ClientReportedData.HighestPing * 10;
                ping.S2CAverageCurrent = lagStats.ClientReportedData.S2CAverageCurrent * 10;
                ping.S2CSlowTotal = lagStats.ClientReportedData.S2CSlowTotal;
                ping.S2CFastTotal = lagStats.ClientReportedData.S2CFastTotal;
                ping.S2CSlowCurrent = lagStats.ClientReportedData.S2CSlowCurrent;
                ping.S2CFastCurrent = lagStats.ClientReportedData.S2CFastCurrent;
            }
        }

        void ILagQuery.QueryReliablePing(Player player, out PingSummary ping)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                ping = default;
                return;
            }

            lock (lagStats.Lock)
            {
                lagStats.ReliablePing.GetSummary(out ping);
            }
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

        void ILagQuery.QueryPacketloss(Player player, out PacketlossSummary packetloss)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                packetloss = default;
                return;
            }

            lagStats.QueryPacketloss(out packetloss);
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
                uint? c2s;

                lock (lagStats.Lock)
                {
                    c2s = lagStats.TimeSync.C2SLatencyEstimate;
                }

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

        int ILagQuery.GetC2SPositionHistogram(Player player, bool sample, ICollection<PingHistogramBucket> data)
        {
            if (player is null || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
                return 0;

            return lagStats.GetC2SPositionHistogram(data, sample);
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

        int ILagQuery.GetSendRoutePercent(Player player)
        {
            if (player is null)
                return 500;

            return GetSendRoutePercent(player);
        }

        bool ILagQuery.DynamicSendRoutePercentEnabled => _dynamicSendRoutePercentEnabled;

        bool ILagQuery.TryGetDynamicSendRoutePercentData(
            Player player,
            out int sendRoutePercent,
            out DateTime? lastUpdated,
            out TimeSpan? rtt,
            out int c2sSampleMin,
            out long sampleCount,
            out long minSampleSize)
        {
            if (!_dynamicSendRoutePercentEnabled
                || player is null 
                || !player.TryGetExtraData(_lagkey, out PlayerLagStats? lagStats))
            {
                sendRoutePercent = default;
                lastUpdated = null;
                rtt = default;
                c2sSampleMin = default;
                sampleCount = default;
                minSampleSize = default;
                return false;
            }

            int? sendRoutePercentOverride;

            lock (lagStats.Lock)
            {
                sendRoutePercentOverride = lagStats.SendRoutePercentOverride;
                lastUpdated = lagStats.SendRoutePercentLastUpdated;
                rtt = lagStats.TimeSync.GetMinRTT();
                c2sSampleMin = lagStats.PositionStats.C2SSampleMinimum;
                sampleCount = lagStats.PositionStats.C2SSampleCount;
                minSampleSize = _dynamicSendRoutePercentC2SMinSampleSize;
            }

            sendRoutePercent = sendRoutePercentOverride ?? GetSendRoutePercent(player);
            return true;
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
            /// Maximum ping in milliseconds
            /// </summary>
            public int Max;

            /// <summary>
            /// Minimum ping in milliseconds
            /// </summary>
            public int Min;

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
                Max = 0;
                Min = 0;
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
                ServerPing.AddValue(newSample.ServerRTT * 10);
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

            public bool RefreshC2SLatencyEstimate(int sendRoutePercent)
            {
                if (_minRoundtripResultIndex is null)
                    return false;

                uint newEstimate = (uint)(_samples[_minRoundtripResultIndex!.Value].ServerRTT * sendRoutePercent / 1000);
                
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

            public TimeSpan? GetMinRTT()
            {
                if (_samplesCount == 0)
                    return null; // no data yet

                TimeSpan? min = null;

                for (int i = _samplesCount - 1; i >= 0; i--)
                {
                    ref readonly TimeSyncSample sample = ref _samples[(_samplesHead + i) % _samples.Length];

                    if (min is null || sample.ServerTimestampRTT < min)
                        min = sample.ServerTimestampRTT;
                }

                return min;
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

        /// <summary>
        /// Welford's online algorithm for calculating variance.
        /// https://en.wikipedia.org/wiki/Algorithms_for_calculating_variance#Welford's_online_algorithm
        /// </summary>
        private class WelfordVarianceCalculator
        {
            /// <summary>
            /// The # of data points.
            /// </summary>
            public long Count { get; private set; } = 0;

            /// <summary>
            /// The running mean.
            /// </summary>
            public double Mean { get; private set; } = 0.0;

            /// <summary>
            /// Sum of squared differences from the mean.
            /// </summary>
            private double _m2 = 0.0;

            /// <summary>
            /// Gets the variance for a population. Divisor is the <see cref="Count"/>.
            /// </summary>
            public double PopulationVariance => Count > 0 ? _m2 / Count : 0.0;

            /// <summary>
            /// Gets the variance for a population. Divisor is <see cref="Count"/> - 1.
            /// </summary>
            public double SampleVariance => Count > 1 ? _m2 / (Count - 1) : 0.0;

            public void AddValue(double value)
            {
                Count++;
                double delta = value - Mean;
                Mean += delta / Count;
                double delta2 = value - Mean;
                _m2 += delta * delta2;
            }

            public void Reset()
            {
                Count = 0;
                Mean = 0.0;
                _m2 = 0.0;
            }
        }

        private class PositionStats
        {
            private readonly PingStats _c2sPopulationStats = new(BucketWidth, PositionBucketCount, -(BucketWidth * PositionBucketCount / 2));
            private readonly WelfordVarianceCalculator _c2sPopulationCalculator = new();
            private readonly Histogram _c2sSampleHistogram = new(BucketWidth, PositionBucketCount, -(BucketWidth * PositionBucketCount / 2));
            public long C2SSampleCount { get; private set; }
            public int C2SSampleMinimum { get; private set; }
            private readonly PingStats _clientReportedS2CLatencyStats = new(BucketWidth, BucketCount, 0);

            public long C2SPopulationCount => _c2sPopulationCalculator.Count;
            public double C2SPopulationMean => _c2sPopulationCalculator.Mean;
            public double C2SPopulationVariance => _c2sPopulationCalculator.SampleVariance; // all the data we have is still a finite subset of an infinite stream of data, use sample variance

            private const double ZFilterThreshold = 3.0;

            public void Add(int c2sLatency, ushort? clientS2CLatency)
            {
                _c2sPopulationStats.AddValue(c2sLatency * 10); // convert ticks to ms
                _c2sSampleHistogram.AddValue(c2sLatency * 10); // convert ticks to ms

                double c2sValue = c2sLatency;

                if (C2SPopulationCount > 50)
                {
                    double stdDev = Math.Sqrt(C2SPopulationVariance);
                    if (stdDev > 0.0)
                    {
                        // Use a bounded Z-score filter to limit the effect of outliers (spikes).
                        double z = (c2sLatency - C2SPopulationMean) / stdDev;

                        // Winsorize: https://en.wikipedia.org/wiki/Winsorizing
                        if (z > ZFilterThreshold)
                        {
                            c2sValue = C2SPopulationMean + (ZFilterThreshold * stdDev);
                        }
                        else if(z < -ZFilterThreshold)
                        {
                            c2sValue = C2SPopulationMean - (ZFilterThreshold * stdDev);
                        }
                    }
                }

                _c2sPopulationCalculator.AddValue(c2sValue);

                C2SSampleCount++;
                if (c2sLatency < C2SSampleMinimum)
                    C2SSampleMinimum = c2sLatency;

                if (clientS2CLatency is not null)
                    _clientReportedS2CLatencyStats.AddValue(clientS2CLatency.Value * 10); // convert ticks to ms
            }

            public void Reset()
            {
                _c2sPopulationStats.Reset();
                _c2sPopulationCalculator.Reset();
                ResetSampleStats();
                _clientReportedS2CLatencyStats.Reset();
            }

            public void ResetSampleStats()
            {
                _c2sSampleHistogram.Reset();
                C2SSampleCount = 0;
                C2SSampleMinimum = int.MaxValue;
            }

            public int GetC2SHistogram(ICollection<PingHistogramBucket> data, bool sample = false)
            {
                if (sample)
                    return _c2sSampleHistogram.GetData(data);
                else
                    return _c2sPopulationStats.GetHistogram(data);
            }

            public void GetC2SSummary(out PingSummary summary)
            {
                _c2sPopulationStats.GetSummary(out summary);
            }

            public int GetS2CHistogram(ICollection<PingHistogramBucket> data)
            {
                return _clientReportedS2CLatencyStats.GetHistogram(data);
            }
        }

        private class PlayerLagStats : IResettable
        {
            public readonly PositionStats PositionStats = new();
            public int? SendRoutePercentOverride;
            public DateTime? SendRoutePercentLastUpdated;
            public readonly PingStats ReliablePing = new(BucketWidth, BucketCount, 0);
            public ClientLatencyData ClientReportedData;
            public TimeSyncRequestData Packetloss;
            public readonly TimeSyncStats TimeSync = new();
            public ReliableLagData ReliableLagData;

            /// <summary>
            /// The latest # of weapon packets that the server sent to the client since entering an arena.
            /// </summary>
            /// <remarks>Synchronized with <see cref="Interlocked"/> methods.</remarks>
            public uint LastWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the start of a security check.
            /// </summary>
            public uint PendingWeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the server sent to the client since entering an arena, as of the last successful security check.
            /// </summary>
            public uint WeaponSentCount;

            /// <summary>
            /// The # of weapon packets that the client reported it received since entering an arena, as of the last successful security check.
            /// </summary>
            public uint WeaponReceiveCount => ClientReportedData.WeaponCount;

            public readonly Lock Lock = new();

            public void Reset()
            {
                lock (Lock)
                {
                    PositionStats.Reset();
                    SendRoutePercentOverride = null;
                    SendRoutePercentLastUpdated = null;
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

            public void QueryTimeSyncPing(out PingSummary clientPing, out PingSummary serverPing)
            {
                lock (Lock)
                {
                    TimeSync.ClientPing.GetSummary(out clientPing);
                    TimeSync.ServerPing.GetSummary(out serverPing);
                }
            }

            public void QueryPacketloss(out PacketlossSummary summary)
            {
                lock (Lock)
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
                lock (Lock)
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
                lock (Lock)
                {
                    data = ReliableLagData;
                }
            }

            public void QueryTimeSyncHistory(ICollection<TimeSyncRecord> records)
            {
                lock (Lock)
                {
                    TimeSync.GetHistory(records);
                }
            }

            public void QueryTimeSyncDriftTicks(out int? clientDrift, out int? serverDriftAvg, out double? serverDriftStdDev)
            {
                lock (Lock)
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
                lock (Lock)
                {
                    // Client value is in ticks, convert to milliseconds.
                    clientDrift = ClientReportedData.TimerDrift * 10;

                    // Server values are already in milliseconds.
                    serverDriftAvg = TimeSync.DriftAvg;
                    serverDriftStdDev = TimeSync.DriftStdDev;
                }
            }

            public int GetC2SPositionHistogram(ICollection<PingHistogramBucket> data, bool sample)
            {
                lock (Lock)
                {
                    return PositionStats.GetC2SHistogram(data, sample);
                }
            }

            public int GetS2CPositionHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (Lock)
                {
                    return PositionStats.GetS2CHistogram(data);
                }
            }

            public int GetReliablePingHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (Lock)
                {
                    return ReliablePing.GetHistogram(data);
                }
            }

            public int GetClientTimeSyncHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (Lock)
                {
                    return TimeSync.ClientPing.GetHistogram(data);
                }
            }

            public int GetServerTimeSyncHistogram(ICollection<PingHistogramBucket> data)
            {
                lock (Lock)
                {
                    return TimeSync.ServerPing.GetHistogram(data);
                }
            }
        }

        #endregion
    }
}
