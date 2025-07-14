﻿using Google.Protobuf;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SSProto = SS.Core.Persist.Protobuf;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module for tracking player stats.
    /// Stats are tracked globally (zone-wide) or per-arena.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To manage global stats, use <see cref="IGlobalPlayerStats"/>.
    /// To manage arena stats, use <see cref="IArenaPlayerStats"/>.
    /// To manage both global and per-arena stats simultaneously, use <see cref="IAllPlayerStats"/>.
    /// </para>
    /// <para>
    /// See <see cref="StatCodes"/> for built-in stats and their associated <see cref="StatId"/>s.
    /// To add custom stats simply use a unique number for your StatId that isn't used yet.
    /// It is best to start with a large number so that you have no chance of colliding if any additional built-in stats are added.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Stats : IAsyncModule, IGlobalPlayerStats, IArenaPlayerStats, IAllPlayerStats, IScoreStats, IStatsAdvisor
    {
        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly INetwork _network;
        private readonly IPersist _persist;
        private readonly IPlayerData _playerData;

        private AdvisorRegistrationToken<IStatsAdvisor>? _iStatsAdvisorToken;

        private InterfaceRegistrationToken<IGlobalPlayerStats>? _iGlobalPlayerStatsToken;
        private InterfaceRegistrationToken<IArenaPlayerStats>? _iArenaPlayerStatsToken;
        private InterfaceRegistrationToken<IAllPlayerStats>? _iAllPlayerStatsToken;
        private InterfaceRegistrationToken<IScoreStats>? _iScoreStatsToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private readonly HashSet<PersistInterval> _intervals =
        [
            PersistInterval.Forever,
            PersistInterval.Reset,
            PersistInterval.Game,
        ];

        private readonly List<DelegatePersistentData<Player, (PersistInterval, PersistScope)>> _persistRegisteredList = [];

        public Stats(
            IComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            INetwork network,
            IPersist persist,
            IPlayerData playerData)
        {
            _broker = broker;
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        [ConfigHelp("Stats", "AdditionalIntervals", ConfigScope.Global,
            Description = $"""
                By default {nameof(Stats)} module tracks intervals: forever, reset, and game.
                This setting allows tracking of additional intervals.
                """)]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _commandManager.AddCommand("stats", Command_stats);

            // TODO: maybe add an interface method to add intervals instead? would need to redo how the stats dictionary is created/retrieved
            string? additionalIntervals = _configManager.GetStr(_configManager.Global, "Stats", "AdditionalIntervals");
            if (!string.IsNullOrWhiteSpace(additionalIntervals))
            {
                foreach (string intervalStr in additionalIntervals.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Enum.TryParse(intervalStr, out PersistInterval interval))
                        _intervals.Add(interval);
                }
            }

            foreach (PersistInterval interval in _intervals)
            {
                // arena
                DelegatePersistentData<Player, (PersistInterval, PersistScope)> registration =
                    new((int)PersistKey.Stats, interval, PersistScope.PerArena, (interval, PersistScope.PerArena), GetPersistData, SetPersistData, ClearPersistData);

                await _persist.RegisterPersistentDataAsync(registration);
                _persistRegisteredList.Add(registration);

                // global
                registration =
                    new((int)PersistKey.Stats, interval, PersistScope.Global, (interval, PersistScope.Global), GetPersistData, SetPersistData, ClearPersistData);

                await _persist.RegisterPersistentDataAsync(registration);
                _persistRegisteredList.Add(registration);
            }

            PersistIntervalEndedCallback.Register(broker, Callback_PersistIntervalEnded);
            NewPlayerCallback.Register(broker, Callback_NewPlayer);

            _iStatsAdvisorToken = broker.RegisterAdvisor<IStatsAdvisor>(this);

            _iGlobalPlayerStatsToken = broker.RegisterInterface<IGlobalPlayerStats>(this);
            _iArenaPlayerStatsToken = broker.RegisterInterface<IArenaPlayerStats>(this);
            _iAllPlayerStatsToken = broker.RegisterInterface<IAllPlayerStats>(this);
            _iScoreStatsToken = broker.RegisterInterface<IScoreStats>(this);
            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iGlobalPlayerStatsToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iArenaPlayerStatsToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iAllPlayerStatsToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iScoreStatsToken) != 0)
                return false;

            broker.UnregisterAdvisor(ref _iStatsAdvisorToken);

            PersistIntervalEndedCallback.Unregister(broker, Callback_PersistIntervalEnded);
            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);

            foreach (var registration in _persistRegisteredList)
            {
                await _persist.UnregisterPersistentDataAsync(registration);
            }
            _persistRegisteredList.Clear();

            _commandManager.RemoveCommand("stats", Command_stats);

            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IGlobalPlayerStats members

        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);

        void IGlobalPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetNumberStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetNumberStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetNumberStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetNumberStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetTimestampStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.Global, player, statCode.StatId, interval, value);

        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value) => TryGetNumberStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value) => TryGetNumberStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value) => TryGetNumberStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value) => TryGetNumberStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value) => TryGetTimestampStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, out TimeSpan value) => TryGetTimerStat(StatScope.Global, player, statCode.StatId, interval, out value);

        void IGlobalPlayerStats.StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StartTimer(StatScope.Global, player, statCode.StatId, interval);
        void IGlobalPlayerStats.StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StopTimer(StatScope.Global, player, statCode.StatId, interval);
        void IGlobalPlayerStats.ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => ResetTimer(StatScope.Global, player, statCode.StatId, interval);

        #endregion

        #region IArenaPlayerStats members

        void IArenaPlayerStats.IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);

        void IArenaPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetNumberStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetNumberStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetNumberStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetNumberStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetTimestampStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.Arena, player, statCode.StatId, interval, value);

        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value) => TryGetNumberStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value) => TryGetNumberStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value) => TryGetNumberStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value) => TryGetNumberStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value) => TryGetTimestampStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, out TimeSpan value) => TryGetTimerStat(StatScope.Arena, player, statCode.StatId, interval, out value);

        void IArenaPlayerStats.StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StartTimer(StatScope.Arena, player, statCode.StatId, interval);
        void IArenaPlayerStats.StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StopTimer(StatScope.Arena, player, statCode.StatId, interval);
        void IArenaPlayerStats.ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => ResetTimer(StatScope.Arena, player, statCode.StatId, interval);

        #endregion

        #region IAllPlayerStats members

        void IAllPlayerStats.IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);
        void IAllPlayerStats.IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);
        void IAllPlayerStats.IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);
        void IAllPlayerStats.IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);
        void IAllPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);

        void IAllPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetNumberStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetNumberStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetNumberStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetNumberStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetTimestampStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.All, player, statCode.StatId, interval, value);

        void IAllPlayerStats.StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StartTimer(StatScope.All, player, statCode.StatId, interval);
        void IAllPlayerStats.StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StopTimer(StatScope.All, player, statCode.StatId, interval);
        void IAllPlayerStats.ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => ResetTimer(StatScope.All, player, statCode.StatId, interval);

        #endregion

        #region IScoreStats members

        void IScoreStats.GetScores(Player player, out int killPoints, out int flagPoints, out ushort kills, out ushort deaths)
        {
            IArenaPlayerStats arenaPlayerStats = this;

            unchecked
            {
                killPoints = arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Reset, out long killPointsValue) ? (int)killPointsValue : default;
                flagPoints = arenaPlayerStats.TryGetStat(player, StatCodes.FlagPoints, PersistInterval.Reset, out long flagPointsValue) ? (int)flagPointsValue : default;
                kills = arenaPlayerStats.TryGetStat(player, StatCodes.Kills, PersistInterval.Reset, out ulong killsValue) ? (ushort)killsValue : default;
                deaths = arenaPlayerStats.TryGetStat(player, StatCodes.Deaths, PersistInterval.Reset, out ulong deathsValue) ? (ushort)deathsValue : default;
            }
        }

        void IScoreStats.SendUpdates(Arena? arena, Player? exclude)
        {
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Status == PlayerState.Playing
                        && (arena == null || player.Arena == arena)
                        && player != exclude)
                    {
                        if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                            continue;

                        // ASSS checks if the player has any stat that is dirty.
                        // Instead, this checks if any of the 4 basic score stats is dirty,
                        // since that's what the 0x03 PlayerEnter and 0x09 ScoreUpdate packets contain.

                        int killPoints = 0;
                        int flagPoints = 0;
                        ushort kills = 0;
                        ushort deaths = 0;
                        bool isDirty = false;

                        lock (pd.Lock)
                        {
                            var stats = GetArenaStatsByInterval(pd, PersistInterval.Reset); // Scores are only for the Reset
                            if (stats == null)
                                continue;

                            GetStatValueAndCheckDirty(stats, StatCodes.KillPoints, ref killPoints, ref isDirty);
                            GetStatValueAndCheckDirty(stats, StatCodes.FlagPoints, ref flagPoints, ref isDirty);
                            GetStatValueAndCheckDirty(stats, StatCodes.Kills, ref kills, ref isDirty);
                            GetStatValueAndCheckDirty(stats, StatCodes.Deaths, ref deaths, ref isDirty);
                        }

                        if (isDirty)
                        {
                            // Update the player's 0x03 PlayerEnter packet.
                            player.Packet.KillPoints = killPoints;
                            player.Packet.FlagPoints = flagPoints;
                            player.Packet.Wins = kills;
                            player.Packet.Losses = deaths;

                            // Send the update to the arena.
                            S2C_ScoreUpdate packet = new(
                                (short)player.Id,
                                killPoints,
                                flagPoints,
                                kills,
                                deaths);

                            _network.SendToArena(arena, exclude, ref packet, NetSendFlags.Reliable); // ASSS sends this unreliably
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            static void GetStatValueAndCheckDirty<TStat, TScore>(SortedDictionary<int, BaseStatInfo> stats, StatCode<TStat> statCode, ref TScore value, ref bool isDirty)
                where TStat : struct, INumber<TStat>
                where TScore : struct, INumber<TScore>
            {
                if (stats.TryGetValue(statCode.StatId, out BaseStatInfo? statInfo)
                    && statInfo is NumberStatInfo<TStat> longStatInfo)
                {
                    value = TScore.CreateTruncating(longStatInfo.Value);

                    if (statInfo.IsDirty)
                    {
                        isDirty = true;
                        statInfo.IsDirty = false;
                    }
                }
            }
        }

        void IScoreStats.ScoreReset(Player player, PersistInterval interval)
        {
            if (player is null)
                return;

            _playerData.Lock();
            try
            {
                ScoreReset(player, interval, DateTime.UtcNow, true);
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        void IScoreStats.ScoreReset(Arena arena, PersistInterval interval)
        {
            if (arena is null)
                return;

            DateTime asOf = DateTime.UtcNow;

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena)
                        continue;

                    ScoreReset(player, interval, asOf, false);
                }

                if (interval == PersistInterval.Reset)
                {
                    // Tell all clients in the arena to reset scores (kill points, flag points, kills, deaths) of all players.
                    S2C_ScoreReset scoreReset = new(-1); // -1 means all players in the arena
                    _network.SendToArena(arena, null, ref scoreReset, NetSendFlags.Reliable);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void ScoreReset(Player player, PersistInterval interval, DateTime asOf, bool sendPacket)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                var stats = GetArenaStatsByInterval(pd, interval);
                if (stats == null)
                    return;

                foreach ((int statId, BaseStatInfo statInfo) in stats)
                {
                    if (statInfo is TimerStatInfo timerStatInfo)
                    {
                        // Keep timers running.
                        // If the timer was running while this happens, only the time from this point will be counted.
                        // The time from the timer start to this point will be discarded.
                        timerStatInfo.Set(TimeSpan.Zero, asOf);
                    }
                    else
                    {
                        statInfo.Clear();
                    }

                    statInfo.IsDirty = !(interval == PersistInterval.Reset
                        && (statId == StatCodes.KillPoints.StatId
                            || statId == StatCodes.FlagPoints.StatId
                            || statId == StatCodes.Kills.StatId
                            || statId == StatCodes.Deaths.StatId
                        ));
                }

                if (interval == PersistInterval.Reset)
                {
                    // Update the player's 0x03 PlayerEnter packet.
                    player.Packet.KillPoints = 0;
                    player.Packet.FlagPoints = 0;
                    player.Packet.Wins = 0;
                    player.Packet.Losses = 0;

                    if (sendPacket)
                    {
                        Arena? arena = player.Arena;
                        if (arena is null)
                            return;

                        // Tell all clients in the arena to reset scores (kill points, flag points, kills, deaths) of the player.
                        S2C_ScoreReset scoreReset = new((short)player.Id);
                        _network.SendToArena(arena, null, ref scoreReset, NetSendFlags.Reliable);
                    }
                }
            }
        }

        #endregion

        #region IStatsAdvisor

        string? IStatsAdvisor.GetStatName(int statId)
        {
            StatId statCode = (StatId)statId;
            if (!Enum.IsDefined(statCode))
                return null;

            return statCode switch
            {
                StatId.KillPoints => "kill points",
                StatId.FlagPoints => "flag points",
                StatId.Kills => "kills",
                StatId.Deaths => "deaths",
                StatId.Assists => "assists",
                StatId.TeamKills => "team kills",
                StatId.TeamDeaths => "team deaths",
                StatId.ArenaTotalTime => "total time",
                StatId.ArenaSpecTime => "spec time",
                StatId.DamageTaken => "damage taken",
                StatId.DamageDealt => "damage dealt",
                StatId.FlagPickups => "flag pickups",
                StatId.FlagCarryTime => "flag time",
                StatId.FlagDrops => "flag drops",
                StatId.FlagNeutDrops => "flag neutral drops",
                StatId.FlagKills => "flag kills",
                StatId.FlagDeaths => "flag deaths",
                StatId.FlagGamesWon => "flag games won",
                StatId.FlagGamesLost => "flag games lost",
                StatId.TurfTags => "turf flag tags",
                StatId.BallCarries => "ball carries",
                StatId.BallCarryTime => "ball time",
                StatId.BallGoals => "goals",
                StatId.BallGamesWon => "ball games won",
                StatId.BallGamesLost => "ball games lost",
                StatId.KothGamesWon => "koth games won",
                StatId.SpeedGamesWon => "speed games won",
                StatId.SpeedPersonalBest => "speed personal best",
                StatId.BallAssists => "assists",
                StatId.BallSteals => "steals",
                StatId.BallDelayedSteals => "delayed steals",
                StatId.BallTurnovers => "turnovers",
                StatId.BallDelayedTurnovers => "delayed turnovers",
                StatId.BallSaves => "saves",
                StatId.BallChokes => "chokes",
                StatId.BallKills => "kills",
                StatId.BallTeamKills => "team kills",
                StatId.BallSpawns => "ball spawns",
                _ => null,
            };
        }

        #endregion

        private void DoNumberStatOperation<T, TState>(StatScope scope, Player player, int statId, PersistInterval? interval, Action<NumberStatInfo<T>, TState> operationCallback, TState state) where T : struct, INumber<T>
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if (interval == null)
                {
                    // all intervals
                    foreach (PersistInterval persistInterval in _intervals)
                    {
                        DoNumberStatOperation(scope, player, statId, persistInterval, operationCallback, state);
                    }
                }
                else
                {
                    if ((scope & StatScope.Global) == StatScope.Global)
                    {
                        // global
                        var stats = GetGlobalStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback, state);
                    }

                    if ((scope & StatScope.Arena) == StatScope.Arena)
                    {
                        // arena
                        var stats = GetArenaStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback, state);
                    }
                }
            }

            void DoOperation(SortedDictionary<int, BaseStatInfo> stats, int statId, Action<NumberStatInfo<T>, TState> operationCallback, TState state)
            {
                NumberStatInfo<T>? statInfo = GetOrCreateNumberStat<T>(stats, statId);
                if (statInfo != null)
                {
                    operationCallback(statInfo, state);
                }
            }
        }

        private void DoTimerStatOperation<TState>(StatScope scope, Player player, int statId, PersistInterval? interval, Action<TimerStatInfo, TState> operationCallback, TState state)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if (interval == null)
                {
                    // all intervals
                    foreach (PersistInterval persistInterval in _intervals)
                    {
                        DoTimerStatOperation(scope, player, statId, persistInterval, operationCallback, state);
                    }
                }
                else
                {
                    if ((scope & StatScope.Global) == StatScope.Global)
                    {
                        // global
                        var stats = GetGlobalStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback, state);
                    }

                    if ((scope & StatScope.Arena) == StatScope.Arena)
                    {
                        // arena
                        var stats = GetArenaStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback, state);
                    }
                }
            }

            void DoOperation(SortedDictionary<int, BaseStatInfo> stats, int statId, Action<TimerStatInfo, TState> operationCallback, TState state)
            {
                TimerStatInfo? timerStatInfo;
                if (stats.TryGetValue(statId, out BaseStatInfo? statInfo))
                {
                    timerStatInfo = statInfo as TimerStatInfo;
                    if (timerStatInfo == null)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Stats), $"Attempted to operate on timer stat {statId}, but it was not a timer.");
                        return;
                    }
                }
                else
                {
                    timerStatInfo = new TimerStatInfo();
                    stats.Add(statId, timerStatInfo);
                }

                operationCallback(timerStatInfo, state);
            }
        }

        private void IncrementStat<T>(StatScope scope, Player player, StatCode<T> statCode, PersistInterval? interval, T amount) where T : struct, INumber<T>
        {
            DoNumberStatOperation<T, T>(scope, player, statCode.StatId, interval, Increment, amount);

            static void Increment(NumberStatInfo<T> statInfo, T amount)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount)
        {
            DoTimerStatOperation(scope, player, statCode.StatId, interval, Increment, amount);

            static void Increment(TimerStatInfo timerStatInfo, TimeSpan amount)
            {
                timerStatInfo.Add(amount);
                timerStatInfo.IsDirty = true;
            }
        }

        private NumberStatInfo<T>? GetOrCreateNumberStat<T>(SortedDictionary<int, BaseStatInfo> stats, int statId) where T : struct, INumber<T>
        {
            NumberStatInfo<T>? statInfo;

            if (stats.TryGetValue(statId, out BaseStatInfo? baseStatInfo))
            {
                statInfo = baseStatInfo as NumberStatInfo<T>;
                if (statInfo == null)
                {
                    // Try to convert the stat to the correct type.
                    if (baseStatInfo is NumberStatInfo<int> int32StatInfo)
                    {
                        statInfo = new NumberStatInfo<T>
                        {
                            Value = T.CreateTruncating(int32StatInfo.Value)
                        };
                    }
                    else if (baseStatInfo is NumberStatInfo<uint> uint32StatInfo)
                    {
                        statInfo = new NumberStatInfo<T>
                        {
                            Value = T.CreateTruncating(uint32StatInfo.Value)
                        };
                    }
                    else if (baseStatInfo is NumberStatInfo<long> int64StatInfo)
                    {
                        statInfo = new NumberStatInfo<T>
                        {
                            Value = T.CreateTruncating(int64StatInfo.Value)
                        };
                    }
                    else if (baseStatInfo is NumberStatInfo<long> uint64StatInfo)
                    {
                        statInfo = new NumberStatInfo<T>
                        {
                            Value = T.CreateTruncating(uint64StatInfo.Value)
                        };
                    }

                    if (statInfo is not null)
                    {
                        // Replace the stat with the correct type.
                        stats[statId] = statInfo;
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Stats), $"Stat {statId} already exists, but is not a {typeof(T).Name}. This is an indication of a programming mistake.");
                    }
                }
            }
            else
            {
                statInfo = new NumberStatInfo<T>();
                stats.Add(statId, statInfo);
            }

            return statInfo;
        }

        private void SetNumberStat<T>(StatScope scope, Player player, int statId, PersistInterval interval, T value) where T : struct, INumber<T>
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if ((scope & StatScope.Global) == StatScope.Global)
                {
                    var stats = GetGlobalStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }

                if ((scope & StatScope.Arena) == StatScope.Arena)
                {
                    var stats = GetArenaStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }
            }

            void SetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, T value)
            {
                NumberStatInfo<T>? statInfo = GetOrCreateNumberStat<T>(stats, statId);
                if (statInfo != null)
                {
                    statInfo.Value = value;
                    statInfo.IsDirty = true;
                }
            }
        }

        private void SetTimestampStat(StatScope scope, Player player, int statId, PersistInterval interval, DateTime value)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                if ((scope & StatScope.Global) == StatScope.Global)
                {
                    var stats = GetGlobalStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }

                if ((scope & StatScope.Arena) == StatScope.Arena)
                {
                    var stats = GetArenaStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }
            }

            void SetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, DateTime value)
            {
                TimestampStatInfo? timestampStatInfo;
                if (stats.TryGetValue(statId, out BaseStatInfo? statInfo))
                {
                    timestampStatInfo = statInfo as TimestampStatInfo;
                    if (timestampStatInfo == null)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Stats), $"Attempted to set timer stat {statId}, but it was not a timer.");
                        return;
                    }

                    timestampStatInfo.Value = value;
                }
                else
                {
                    timestampStatInfo = new TimestampStatInfo
                    {
                        Value = value
                    };
                    stats.Add(statId, timestampStatInfo);
                }
            }
        }

        private void SetTimerStat(StatScope scope, Player player, int statId, PersistInterval interval, TimeSpan value)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                if ((scope & StatScope.Global) == StatScope.Global)
                {
                    var stats = GetGlobalStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }

                if ((scope & StatScope.Arena) == StatScope.Arena)
                {
                    var stats = GetArenaStatsByInterval(pd, interval);
                    if (stats != null)
                        SetStat(stats, statId, value);
                }
            }

            void SetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, TimeSpan value)
            {
                TimerStatInfo? timerStatInfo;
                if (stats.TryGetValue(statId, out BaseStatInfo? statInfo))
                {
                    timerStatInfo = statInfo as TimerStatInfo;
                    if (timerStatInfo == null)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Stats), $"Attempted to set timer stat {statId}, but it was not a timer.");
                        return;
                    }

                    timerStatInfo.Set(value, now);
                }
                else
                {
                    timerStatInfo = new TimerStatInfo(value);
                    stats.Add(statId, timerStatInfo);
                }
            }
        }

        private bool TryGetNumberStat<T>(StatScope scope, Player player, int statId, PersistInterval interval, out T value) where T : struct, INumber<T>
        {
            // This method only can return 1 value, so it does not allow a combined scope.
            if (scope != StatScope.Global && scope != StatScope.Arena)
                throw new ArgumentException("Only Global or Arena scope are allowed. Combined scopes are not allowed", nameof(scope));

            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                value = default;
                return false;
            }

            lock (pd.Lock)
            {
                if (scope == StatScope.Global)
                {
                    return TryGetStat(GetGlobalStatsByInterval(pd, interval), statId, out value);
                }
                else
                {
                    return TryGetStat(GetArenaStatsByInterval(pd, interval), statId, out value);
                }
            }

            static bool TryGetStat(SortedDictionary<int, BaseStatInfo>? stats, int statId, out T value)
            {
                if (stats == null)
                {
                    value = default;
                    return false;
                }

                if (stats.TryGetValue(statId, out BaseStatInfo? baseStatInfo)
                    && baseStatInfo is NumberStatInfo<T> statInfo)
                {
                    value = statInfo.Value;
                    return true;
                }
                else
                {
                    value = default;
                    return false;
                }
            }
        }

        private bool TryGetTimestampStat(StatScope scope, Player player, int statId, PersistInterval interval, out DateTime value)
        {
            // This method only can return 1 value, so it does not allow a combined scope.
            if (scope != StatScope.Global && scope != StatScope.Arena)
                throw new ArgumentException("Only Global or Arena scope are allowed. Combined scopes are not allowed", nameof(scope));

            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                value = default;
                return false;
            }

            lock (pd.Lock)
            {
                if (scope == StatScope.Global)
                {
                    return TryGetStat(GetGlobalStatsByInterval(pd, interval), statId, out value);
                }
                else
                {
                    return TryGetStat(GetArenaStatsByInterval(pd, interval), statId, out value);
                }
            }

            static bool TryGetStat(SortedDictionary<int, BaseStatInfo>? stats, int statId, out DateTime value)
            {
                if (stats == null)
                {
                    value = default;
                    return false;
                }

                if (stats.TryGetValue(statId, out BaseStatInfo? baseStatInfo)
                    && baseStatInfo is TimestampStatInfo timestampStatInfo)
                {
                    value = timestampStatInfo.Value;
                    return true;
                }
                else
                {
                    value = default;
                    return false;
                }
            }
        }

        private bool TryGetTimerStat(StatScope scope, Player player, int statId, PersistInterval interval, out TimeSpan value)
        {
            // This method only can return 1 value, so it does not allow a combined scope.
            if (scope != StatScope.Global && scope != StatScope.Arena)
                throw new ArgumentException("Only Global or Arena scope are allowed. Combined scopes are not allowed", nameof(scope));

            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                value = default;
                return false;
            }

            lock (pd.Lock)
            {
                if (scope == StatScope.Global)
                {
                    return TryGetStat(GetGlobalStatsByInterval(pd, interval), statId, out value);
                }
                else
                {
                    return TryGetStat(GetArenaStatsByInterval(pd, interval), statId, out value);
                }
            }

            static bool TryGetStat(SortedDictionary<int, BaseStatInfo>? stats, int statId, out TimeSpan value)
            {
                if (stats == null)
                {
                    value = default;
                    return false;
                }

                if (stats.TryGetValue(statId, out BaseStatInfo? baseStatInfo)
                    && baseStatInfo is TimerStatInfo timerStatInfo)
                {
                    value = timerStatInfo.GetValueAsOf(DateTime.UtcNow);
                    return true;
                }
                else
                {
                    value = default;
                    return false;
                }
            }
        }

        private void StartTimer(StatScope scope, Player player, int statId, PersistInterval? interval)
        {
            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer, DateTime.UtcNow);

            static void DoStartTimer(TimerStatInfo timerStatInfo, DateTime now)
            {
                timerStatInfo.Start(now);
            }
        }

        private void StopTimer(StatScope scope, Player player, int statId, PersistInterval? interval)
        {
            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer, DateTime.UtcNow);

            static void DoStartTimer(TimerStatInfo timerStatInfo, DateTime now)
            {
                timerStatInfo.Stop(now);
            }
        }

        private void ResetTimer(StatScope scope, Player player, int statId, PersistInterval? interval)
        {
            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer, (object?)null);

            static void DoStartTimer(TimerStatInfo timerStatInfo, object? dummy)
            {
                timerStatInfo.Reset();
            }
        }

        #region Persist methods

        private void GetPersistData(Player? player, Stream outStream, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            SSProto.PlayerStats playerStats;

            lock (pd.Lock)
            {
                SortedDictionary<int, BaseStatInfo>? stats = state.Scope switch
                {
                    PersistScope.PerArena => GetArenaStatsByInterval(pd, state.Interval),
                    PersistScope.Global => GetGlobalStatsByInterval(pd, state.Interval),
                    _ => null
                };

                if (stats == null)
                    return;

                DateTime now = DateTime.UtcNow;
                playerStats = new();

                foreach ((int key, BaseStatInfo baseStatInfo) in stats)
                {
                    // TODO: add logic for the other integer encodings (based on sign and value), might save some space?
                    if (baseStatInfo is NumberStatInfo<int> intStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Int32Value = intStatInfo.Value });
                    }
                    else if (baseStatInfo is NumberStatInfo<uint> uintStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Uint32Value = uintStatInfo.Value });
                    }
                    else if (baseStatInfo is NumberStatInfo<long> longStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Int64Value = longStatInfo.Value });
                    }
                    else if (baseStatInfo is NumberStatInfo<ulong> ulongStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Uint64Value = ulongStatInfo.Value });
                    }
                    else if (baseStatInfo is TimestampStatInfo timestampStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(timestampStatInfo.Value) });
                    }
                    else if (baseStatInfo is TimerStatInfo timerStatInfo)
                    {
                        playerStats.StatMap.Add(
                            key,
                            new SSProto.StatInfo() { Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(timerStatInfo.GetValueAsOf(now)) });
                    }
                }
            }

            try
            {
                // serialize stats to outStream
                playerStats.WriteTo(outStream);
            }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Stats), player, $"Error serializing for interval {state.Interval}. {ex.Message}");
                return;
            }
        }

        private void SetPersistData(Player? player, Stream inStream, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            SSProto.PlayerStats playerStats;

            try
            {
                playerStats = SSProto.PlayerStats.Parser.ParseFrom(inStream);
            }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Stats), player, $"Error deserializing for interval {state.Interval}. {ex.Message}");
                return;
            }

            lock (pd.Lock)
            {
                SortedDictionary<int, BaseStatInfo>? stats = state.Scope switch
                {
                    PersistScope.PerArena => GetArenaStatsByInterval(pd, state.Interval),
                    PersistScope.Global => GetGlobalStatsByInterval(pd, state.Interval),
                    _ => null
                };

                if (stats == null)
                    return;

                foreach ((int key, SSProto.StatInfo pStatInfo) in playerStats.StatMap)
                {
                    if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Int32Value)
                    {
                        stats[key] = new NumberStatInfo<int>() { Value = pStatInfo.Int32Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Uint32Value)
                    {
                        stats[key] = new NumberStatInfo<uint>() { Value = pStatInfo.Uint32Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Int64Value)
                    {
                        stats[key] = new NumberStatInfo<long>() { Value = pStatInfo.Int64Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Uint64Value)
                    {
                        stats[key] = new NumberStatInfo<ulong>() { Value = pStatInfo.Uint64Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sint32Value)
                    {
                        stats[key] = new NumberStatInfo<int>() { Value = pStatInfo.Sint32Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sint64Value)
                    {
                        stats[key] = new NumberStatInfo<long>() { Value = pStatInfo.Sint64Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Fixed32Value)
                    {
                        stats[key] = new NumberStatInfo<uint>() { Value = pStatInfo.Fixed32Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Fixed64Value)
                    {
                        stats[key] = new NumberStatInfo<ulong>() { Value = pStatInfo.Fixed64Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sfixed32Value)
                    {
                        stats[key] = new NumberStatInfo<int>() { Value = pStatInfo.Sfixed32Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sfixed64Value)
                    {
                        stats[key] = new NumberStatInfo<long>() { Value = pStatInfo.Sfixed64Value };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Timestamp)
                    {
                        stats[key] = new TimestampStatInfo() { Value = pStatInfo.Timestamp.ToDateTime() };
                    }
                    else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Duration)
                    {
                        stats[key] = new TimerStatInfo(pStatInfo.Duration.ToTimeSpan());
                    }

                    // Older versions of saved KillPoints and FlagPoints as an UInt64. It was later changed to be an Int64.
                    // This is a hacky workaround for switching the type and being compatible with existing records.
                    if (key == StatCodes.KillPoints.StatId || key == StatCodes.FlagPoints.StatId)
                    {
                        if (stats[key] is NumberStatInfo<ulong> uint64Stat)
                        {
                            stats[key] = new NumberStatInfo<long>()
                            {
                                Value = (long)uint64Stat.Value
                            };
                        }
                    }
                }
            }
        }

        private void ClearPersistData(Player? player, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                SortedDictionary<int, BaseStatInfo>? stats = state.Scope switch
                {
                    PersistScope.PerArena => GetArenaStatsByInterval(pd, state.Interval),
                    PersistScope.Global => GetGlobalStatsByInterval(pd, state.Interval),
                    _ => null
                };

                if (stats == null)
                    return;

                foreach (BaseStatInfo stat in stats.Values)
                {
                    stat.Clear();
                }
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = "[-g] [{forever} | {game} | {reset}]",
            Description = """
                Prints out some basic statistics about the target player, or if no
                target, yourself. By default, it will show arena stats. Use -g to switch it to
                show global (zone-wide) stats. An interval name can be specified as an argument.
                By default, the per-reset interval is used.
                """)]
        private void Command_stats(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            PersistScope scope = PersistScope.PerArena;
            PersistInterval interval = PersistInterval.Reset;

            foreach (Range range in parameters.Split(' '))
            {
                ReadOnlySpan<char> token = parameters[range].Trim();
                if (token.IsEmpty)
                    continue;

                if (token.Equals("-g", StringComparison.OrdinalIgnoreCase))
                {
                    scope = PersistScope.Global;
                }
                else
                {
                    if (!Enum.TryParse(token, true, out interval)
                        || !_intervals.Contains(interval))
                    {
                        _chat.SendMessage(player, $"Invalid interval");
                        return;
                    }
                }
            }

            lock (pd.Lock)
            {
                SortedDictionary<int, BaseStatInfo>? stats = scope switch
                {
                    PersistScope.PerArena => GetArenaStatsByInterval(pd, interval),
                    PersistScope.Global => GetGlobalStatsByInterval(pd, interval),
                    _ => null,
                };

                if (stats == null)
                    return;

                _chat.SendMessage(player, $"The server is keeping track of the following {(scope == PersistScope.Global ? "Global" : "Arena")} {interval} stats about {(targetPlayer != player ? targetPlayer.Name : "you")}:");

                DateTime now = DateTime.UtcNow;

                foreach ((int statId, BaseStatInfo baseStatinfo) in stats)
                {
                    string? statName = null;
                    var advisors = _broker.GetAdvisors<IStatsAdvisor>();
                    foreach (IStatsAdvisor advisor in advisors)
                    {
                        statName = advisor.GetStatName(statId);
                        if (!string.IsNullOrWhiteSpace(statName))
                            break;
                    }

                    if (baseStatinfo is NumberStatInfo<int> intStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {intStatInfo.Value}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {intStatInfo.Value}");
                    }
                    else if (baseStatinfo is NumberStatInfo<uint> uintStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {uintStatInfo.Value}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {uintStatInfo.Value}");
                    }
                    else if (baseStatinfo is NumberStatInfo<long> longStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {longStatInfo.Value}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {longStatInfo.Value}");
                    }
                    else if (baseStatinfo is NumberStatInfo<ulong> ulongStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {ulongStatInfo.Value}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {ulongStatInfo.Value}");
                    }
                    else if (baseStatinfo is TimestampStatInfo timestampStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {timestampStatInfo.Value}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {timestampStatInfo.Value}");
                    }
                    else if (baseStatinfo is TimerStatInfo timerStatInfo)
                    {
                        if (string.IsNullOrWhiteSpace(statName))
                            _chat.SendMessage(player, $"  {statId}: {timerStatInfo.GetValueAsOf(now)}");
                        else
                            _chat.SendMessage(player, $"  {statName}: {timerStatInfo.GetValueAsOf(now)}");
                    }
                }
            }
        }

        #endregion

        #region Callbacks

        private void Callback_PersistIntervalEnded(PersistInterval interval, string arenaGroup)
        {
            if (interval == PersistInterval.Reset)
            {
                bool allArenas = string.Equals(arenaGroup, Constants.ArenaGroup_Global);

                _arenaManager.Lock();
                try
                {
                    foreach (Arena arena in _arenaManager.Arenas)
                    {
                        if (allArenas || string.Equals(arenaGroup, _persist.GetScoreGroup(arena), StringComparison.OrdinalIgnoreCase))
                        {
                            ((IScoreStats)this).ScoreReset(arena, interval);
                        }
                    }
                }
                finally
                {
                    _arenaManager.Unlock();
                }
            }
        }

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if (isNew)
                {
                    foreach (PersistInterval interval in _intervals)
                    {
                        if (pd.CurrentArenaStats.TryGetValue(interval, out var stats))
                        {
                            stats.Clear();
                        }
                        else
                        {
                            pd.CurrentArenaStats.Add(interval, []);
                        }
                    }
                }
                else
                {
                    foreach (var stats in pd.CurrentArenaStats.Values)
                    {
                        stats.Clear();
                    }

                    pd.CurrentArenaStats.Clear();
                }
            }
        }

        #endregion

        private static SortedDictionary<int, BaseStatInfo>? GetArenaStatsByInterval(PlayerData pd, PersistInterval interval)
        {
            if (pd == null)
                return null;

            if (pd.CurrentArenaStats.TryGetValue(interval, out SortedDictionary<int, BaseStatInfo>? stats))
                return stats;
            else
                return null;
        }

        private static SortedDictionary<int, BaseStatInfo>? GetGlobalStatsByInterval(PlayerData pd, PersistInterval interval)
        {
            if (pd == null)
                return null;

            if (pd.GlobalStats.TryGetValue(interval, out SortedDictionary<int, BaseStatInfo>? stats))
                return stats;
            else
                return null;
        }

        #region Helper types

        [Flags]
        private enum StatScope
        {
            Global = 1,
            Arena = 2,
            All = 3,
        }

        private class PlayerData : IResettable
        {
            public readonly Dictionary<PersistInterval, SortedDictionary<int, BaseStatInfo>> CurrentArenaStats = new()
            {
                { PersistInterval.Forever, new() },
                { PersistInterval.Reset, new() },
                { PersistInterval.Game, new() },
            };

            public readonly Dictionary<PersistInterval, SortedDictionary<int, BaseStatInfo>> GlobalStats = new()
            {
                { PersistInterval.Forever, new() },
                { PersistInterval.Reset, new() },
                { PersistInterval.Game, new() },
            };

            public readonly Lock Lock = new();

            public bool TryReset()
            {
                lock (Lock)
                {
                    foreach (var dictionary in CurrentArenaStats.Values)
                    {
                        dictionary.Clear();
                    }

                    foreach (var dictionary in GlobalStats.Values)
                    {
                        dictionary.Clear();
                    }
                }

                return true;
            }
        }

        private abstract class BaseStatInfo
        {
            public bool IsDirty;

            public abstract void Clear();
        }

        private class NumberStatInfo<T> : BaseStatInfo where T : struct, INumber<T>
        {
            public T Value { get; set; }

            public override void Clear()
            {
                if (!Value.Equals(default))
                    IsDirty = true;

                Value = default;
            }
        }

        private class TimestampStatInfo : BaseStatInfo
        {
            public DateTime Value { get; set; }

            public override void Clear()
            {
                if (!Value.Equals(default))
                    IsDirty = true;

                Value = default;
            }
        }

        private class TimerStatInfo : BaseStatInfo
        {
            private TimeSpan _elapsed;
            private DateTime? _started;

            public TimerStatInfo()
            {
                _elapsed = TimeSpan.Zero;
                _started = null;
            }

            public TimerStatInfo(TimeSpan elapsed)
            {
                _elapsed = elapsed;
                _started = null;
            }

            public TimeSpan GetValueAsOf(DateTime now) => _elapsed + (_started != null ? now - _started.Value : TimeSpan.Zero);

            public void Start(DateTime now)
            {
                if (_started != null)
                {
                    Update(now, true);
                }
                else
                {
                    _started = now;
                }
            }

            public void Stop(DateTime now)
            {
                Update(now, false);
            }

            public void Reset()
            {
                _elapsed = TimeSpan.Zero;
                _started = null;
            }

            private void Update(DateTime now, bool keepRunning)
            {
                if (_started != null)
                {
                    _elapsed += (now - _started.Value);
                    _started = keepRunning ? now : null;
                }
            }

            public void Set(TimeSpan elapsed, DateTime asOf)
            {
                _elapsed = elapsed;

                if (_started != null)
                {
                    // It was running, keep it running, but consider it started as of now.
                    _started = asOf;
                }
            }

            public void Add(TimeSpan timeSpan)
            {
                _elapsed += timeSpan;
            }

            public override void Clear()
            {
                if (_elapsed != TimeSpan.Zero || _started != null)
                    IsDirty = true;

                Reset();
            }
        }

        #endregion
    }
}
