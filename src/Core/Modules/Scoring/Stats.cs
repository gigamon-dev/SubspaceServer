using Google.Protobuf;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SSProto = SS.Core.Persist.Protobuf;

namespace SS.Core.Modules.Scoring
{
    public class Stats : IModule, IGlobalPlayerStats, IArenaPlayerStats, IAllPlayerStats, IScoreStats
    {
        private ComponentBroker _broker;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private INetwork _network;
        private IPersist _persist;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken _iGlobalPlayerStatsToken;
        private InterfaceRegistrationToken _iArenaPlayerStatsToken;
        private InterfaceRegistrationToken _iAllPlayerStatsToken;
        private InterfaceRegistrationToken _iScoreStatsToken;

        private int _pdKey;

        private readonly HashSet<PersistInterval> _intervals = new()
        {
            PersistInterval.Forever,
            PersistInterval.Reset,
            PersistInterval.Game,
        };

        private readonly List<DelegatePersistentData<Player, (PersistInterval, PersistScope)>> _persistRegisteredList = new();

        #region Module members

        [ConfigHelp("Stats", "AdditionalIntervals", ConfigScope.Global, typeof(string), 
            Description = 
            $"By default {nameof(Stats)} module tracks intervals: forever, reset, and game. " +
            $"This setting allows tracking of additional intervals.")]
        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            INetwork network,
            IPersist persist,
            IPlayerData playerData)
        {
            _broker = broker;
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _commandManager.AddCommand("stats", Command_stats);

            // TODO: maybe add an interface method to add intervals instead? would need to redo how the stats dictionary is created/retrieved
            string additionalIntervals = _configManager.GetStr(_configManager.Global, "Stats", "AdditionalIntervals");
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

                _persist.RegisterPersistentData(registration);
                _persistRegisteredList.Add(registration);

                // global
                registration =
                    new((int)PersistKey.Stats, interval, PersistScope.Global, (interval, PersistScope.Global), GetPersistData, SetPersistData, ClearPersistData);

                _persist.RegisterPersistentData(registration);
                _persistRegisteredList.Add(registration);
            }

            PersistIntervalEndedCallback.Register(broker, Callback_PersistIntervalEnded);
            NewPlayerCallback.Register(broker, Callback_NewPlayer);
            GetStatNameCallback.Register(broker, Callback_GetStatName); // TODO: this might be nicer on an advisor interface (change this when IAdvisor is added to the ComponentBroker)


            _iGlobalPlayerStatsToken = broker.RegisterInterface<IGlobalPlayerStats>(this);
            _iArenaPlayerStatsToken = broker.RegisterInterface<IArenaPlayerStats>(this);
            _iAllPlayerStatsToken = broker.RegisterInterface<IAllPlayerStats>(this);
            _iScoreStatsToken = broker.RegisterInterface<IScoreStats>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IGlobalPlayerStats>(ref _iGlobalPlayerStatsToken);
            broker.UnregisterInterface<IArenaPlayerStats>(ref _iArenaPlayerStatsToken);
            broker.UnregisterInterface<IAllPlayerStats>(ref _iAllPlayerStatsToken);
            broker.UnregisterInterface<IScoreStats>(ref _iScoreStatsToken);

            PersistIntervalEndedCallback.Unregister(broker, Callback_PersistIntervalEnded);
            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);
            GetStatNameCallback.Unregister(broker, Callback_GetStatName);

            foreach (var registration in _persistRegisteredList)
            {
                _persist.UnregisterPersistentData(registration);
            }
            _persistRegisteredList.Clear();

            _commandManager.RemoveCommand("stats", Command_stats);

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region IGlobalPlayerStats members

        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.Global, player, statCode, interval, amount);
        void IGlobalPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementTimerStat(StatScope.Global, player, statCode, interval, amount);

        void IGlobalPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetStat(StatScope.Global, player, statCode.StatId, interval, value);
        void IGlobalPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.Global, player, statCode.StatId, interval, value);

        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value) => TryGetStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value) => TryGetStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value) => TryGetStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value) => TryGetStat(StatScope.Global, player, statCode.StatId, interval, out value);
        bool IGlobalPlayerStats.TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value) => TryGetStat(StatScope.Global, player, statCode.StatId, interval, out value);
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
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.Arena, player, statCode, interval, amount);
        void IArenaPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementTimerStat(StatScope.Arena, player, statCode, interval, amount);

        void IArenaPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetStat(StatScope.Arena, player, statCode.StatId, interval, value);
        void IArenaPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.Arena, player, statCode.StatId, interval, value);

        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value) => TryGetStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value) => TryGetStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value) => TryGetStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value) => TryGetStat(StatScope.Arena, player, statCode.StatId, interval, out value);
        bool IArenaPlayerStats.TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value) => TryGetStat(StatScope.Arena, player, statCode.StatId, interval, out value);
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
        void IAllPlayerStats.IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount) => IncrementStat(StatScope.All, player, statCode, interval, amount);
        void IAllPlayerStats.IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount) => IncrementTimerStat(StatScope.All, player, statCode, interval, amount);

        void IAllPlayerStats.SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value) => SetStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value) => SetStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value) => SetStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value) => SetStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value) => SetStat(StatScope.All, player, statCode.StatId, interval, value);
        void IAllPlayerStats.SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value) => SetTimerStat(StatScope.All, player, statCode.StatId, interval, value);

        void IAllPlayerStats.StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StartTimer(StatScope.All, player, statCode.StatId, interval);
        void IAllPlayerStats.StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => StopTimer(StatScope.All, player, statCode.StatId, interval);
        void IAllPlayerStats.ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval) => ResetTimer(StatScope.All, player, statCode.StatId, interval);

        #endregion

        #region IScoreStats members

        void IScoreStats.GetScores(Player player, out int killPoints, out int flagPoints, out short kills, out short deaths)
        {
            IArenaPlayerStats arenaPlayerStats = this;

            unchecked
            {
                killPoints = arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Reset, out ulong uKillPoints) ? (int)uKillPoints : default;
                flagPoints = arenaPlayerStats.TryGetStat(player, StatCodes.FlagPoints, PersistInterval.Reset, out ulong uFlagPoints) ? (int)uFlagPoints : default;
                kills = arenaPlayerStats.TryGetStat(player, StatCodes.Kills, PersistInterval.Reset, out ulong uKills) ? (short)uKills : default;
                deaths = arenaPlayerStats.TryGetStat(player, StatCodes.Deaths, PersistInterval.Reset, out ulong uDeaths) ? (short)uDeaths : default;
            }
        }

        void IScoreStats.SendUpdates(Arena arena, Player exclude)
        {
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.PlayerList)
                {
                    if (player.Status == PlayerState.Playing
                        && (arena == null || player.Arena == arena)
                        && player != exclude)
                    {
                        if (player[_pdKey] is not PlayerData pd)
                            continue;

                        // ASSS checks if the player has any stat that is dirty.
                        // Instead, this checks if any of the 4 basic score stats is dirty,
                        // since that's what the 0x03 PlayerEnter and 0x09 ScoreUpdate packets contain.

                        int killPoints = 0;
                        int flagPoints = 0;
                        short kills = 0;
                        short deaths = 0;
                        bool isDirty = false;

                        lock (pd.Lock)
                        {
                            var stats = GetArenaStatsByInterval(pd, PersistInterval.Reset); // Scores are only for the Reset
                            if (stats == null)
                                continue;

                            GetStatValueAndCheckDirtyInt32(stats, StatCodes.KillPoints, ref killPoints, ref isDirty);
                            GetStatValueAndCheckDirtyInt32(stats, StatCodes.FlagPoints, ref flagPoints, ref isDirty);
                            GetStatValueAndCheckDirtyInt16(stats, StatCodes.Kills, ref kills, ref isDirty);
                            GetStatValueAndCheckDirtyInt16(stats, StatCodes.Deaths, ref deaths, ref isDirty);
                        }

                        if (isDirty)
                        {
                            // update the player's 0x03 PlayerEnter packet
                            player.Packet.KillPoints = killPoints;
                            player.Packet.FlagPoints = flagPoints;
                            player.Packet.Wins = kills;
                            player.Packet.Losses = deaths;

                            // send and update to the arena
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

            static void GetStatValueAndCheckDirtyInt32(SortedDictionary<int, BaseStatInfo> stats, StatCode<ulong> statCode, ref int value, ref bool isDirty)
            {
                if (stats.TryGetValue(statCode.StatId, out BaseStatInfo statInfo)
                    && statInfo is StatInfo<ulong> longStatInfo)
                {
                    unchecked
                    {
                        value = (int)longStatInfo.Value;
                    }

                    if (statInfo.IsDirty)
                    {
                        isDirty = true;
                        statInfo.IsDirty = false;
                    }
                }
            }

            static void GetStatValueAndCheckDirtyInt16(SortedDictionary<int, BaseStatInfo> stats, StatCode<ulong> statCode, ref short value, ref bool isDirty)
            {
                if (stats.TryGetValue(statCode.StatId, out BaseStatInfo statInfo)
                    && statInfo is StatInfo<ulong> longStatInfo)
                {
                    unchecked
                    {
                        value = (short)longStatInfo.Value;
                    }

                    if (statInfo.IsDirty)
                    {
                        isDirty = true;
                        statInfo.IsDirty = false;
                    }
                }
            }
        }

        void IScoreStats.ScoreReset(Player p, PersistInterval interval)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            var stats = GetArenaStatsByInterval(pd, interval);
            if (stats == null)
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                foreach (BaseStatInfo statInfo in stats.Values)
                {
                    if (statInfo is TimerStatInfo timerStatInfo)
                    {
                        // Keep timers running.
                        // If the timer was running while this happens, only the time from this point will be counted.
                        // The time from the timer start to this point will be discarded.
                        timerStatInfo.Set(TimeSpan.Zero, now);
                    }
                    else
                    {
                        statInfo.Clear();
                    }

                    statInfo.IsDirty = true;
                }
            }
        }

        #endregion

        private void DoStatInfoOperation<T>(StatScope scope, Player player, int statId, PersistInterval? interval, Action<StatInfo<T>> operationCallback) where T : struct, IEquatable<T>
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            lock (pd.Lock)
            {
                if (interval == null)
                {
                    // all intervals
                    foreach (PersistInterval persistInterval in _intervals)
                    {
                        DoStatInfoOperation(scope, player, statId, persistInterval, operationCallback);
                    }
                }
                else
                {
                    if ((scope & StatScope.Global) == StatScope.Global)
                    {
                        // global
                        var stats = GetGlobalStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback);
                    }

                    if ((scope & StatScope.Arena) == StatScope.Arena)
                    {
                        // arena
                        var stats = GetArenaStatsByInterval(pd, interval.Value);
                        if (stats != null)
                            DoOperation(stats, statId, operationCallback);
                    }
                }
            }

            void DoOperation(SortedDictionary<int, BaseStatInfo> stats, int statId, Action<StatInfo<T>> operationCallback)
            {
                StatInfo<T> statInfo = GetOrCreateStatInfo<T>(stats, statId);
                if (statInfo != null)
                {
                    operationCallback(statInfo);
                }
            }
        }

        private void DoTimerStatOperation(StatScope scope, Player player, int statId, PersistInterval? interval, Action<TimerStatInfo> operationCallback)
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            if (interval == null)
            {
                // all intervals
                foreach (PersistInterval persistInterval in _intervals)
                {
                    DoTimerStatOperation(scope, player, statId, persistInterval, operationCallback);
                }
            }
            else
            {
                if ((scope & StatScope.Global) == StatScope.Global)
                {
                    // global
                    var stats = GetGlobalStatsByInterval(pd, interval.Value);
                    if (stats != null)
                        DoOperation(stats, statId, operationCallback);
                }

                if ((scope & StatScope.Arena) == StatScope.Arena)
                {
                    // arena
                    var stats = GetArenaStatsByInterval(pd, interval.Value);
                    if (stats != null)
                        DoOperation(stats, statId, operationCallback);
                }
            }

            void DoOperation(SortedDictionary<int, BaseStatInfo> stats, int statId, Action<TimerStatInfo> operationCallback)
            {
                TimerStatInfo timerStatInfo;
                if (stats.TryGetValue(statId, out BaseStatInfo statInfo))
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

                operationCallback(timerStatInfo);
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<int> statCode, PersistInterval? interval, int amount)
        {
            DoStatInfoOperation<int>(scope, player, statCode.StatId, interval, Increment);

            void Increment(StatInfo<int> statInfo)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount)
        {
            DoStatInfoOperation<uint>(scope, player, statCode.StatId, interval, Increment);

            void Increment(StatInfo<uint> statInfo)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<long> statCode, PersistInterval? interval, long amount)
        {
            DoStatInfoOperation<long>(scope, player, statCode.StatId, interval, Increment);

            void Increment(StatInfo<long> statInfo)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount)
        {
            DoStatInfoOperation<ulong>(scope, player, statCode.StatId, interval, Increment);

            void Increment(StatInfo<ulong> statInfo)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementStat(StatScope scope, Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount)
        {
            DoStatInfoOperation<DateTime>(scope, player, statCode.StatId, interval, Increment);

            void Increment(StatInfo<DateTime> statInfo)
            {
                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        private void IncrementTimerStat(StatScope scope, Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount)
        {
            DoTimerStatOperation(scope, player, statCode.StatId, interval, Increment);

            void Increment(TimerStatInfo timerStatInfo)
            {
                timerStatInfo.Add(amount);
                timerStatInfo.IsDirty = true;
            }
        }

        private StatInfo<T> GetOrCreateStatInfo<T>(SortedDictionary<int, BaseStatInfo> stats, int statId) where T : struct, IEquatable<T>
        {
            StatInfo<T> statInfo;

            if (stats.TryGetValue(statId, out BaseStatInfo baseStatInfo))
            {
                statInfo = baseStatInfo as StatInfo<T>;
                if (statInfo == null)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Stats), $"Stat {statId} already exists, but is not a {typeof(T).Name}. This is an indication of a programming mistake.");
                }
            }
            else
            {
                statInfo = new StatInfo<T>();
                stats.Add(statId, statInfo);
            }

            return statInfo;
        }

        private void SetStat<T>(StatScope scope, Player player, int statId, PersistInterval interval, T value) where T : struct, IEquatable<T>
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

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

            void SetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, T value)
            {
                StatInfo<T> statInfo = GetOrCreateStatInfo<T>(stats, statId);
                if (statInfo != null)
                {
                    statInfo.Value = value;
                    statInfo.IsDirty = true;
                }
            }
        }

        private void SetTimerStat(StatScope scope, Player player, int statId, PersistInterval interval, TimeSpan value)
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            DateTime now = DateTime.UtcNow;

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

            void SetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, TimeSpan value)
            {
                TimerStatInfo timerStatInfo;
                if (stats.TryGetValue(statId, out BaseStatInfo statInfo))
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

        private bool TryGetStat<T>(StatScope scope, Player player, int statId, PersistInterval interval, out T value) where T : struct, IEquatable<T>
        {
            // This method only can return 1 value, so it does not allow a combined scope.
            if (scope != StatScope.Global && scope != StatScope.Arena)
                throw new ArgumentException("Only Global or Arena scope are allowed. Combined scopes are not allowed", nameof(scope));

            if (player == null || player[_pdKey] is not PlayerData pd)
            {
                value = default;
                return false;
            }

            if (scope == StatScope.Global)
            {
                return TryGetStat(GetGlobalStatsByInterval(pd, interval), statId, out value);
            }
            else
            {
                return TryGetStat(GetArenaStatsByInterval(pd, interval), statId, out value);
            }

            static bool TryGetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, out T value)
            {
                if (stats == null)
                {
                    value = default;
                    return false;
                }

                if (stats.TryGetValue(statId, out BaseStatInfo baseStatInfo)
                    && baseStatInfo is StatInfo<T> statInfo)
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

        private bool TryGetTimerStat(StatScope scope, Player player, int statId, PersistInterval interval, out TimeSpan value)
        {
            // This method only can return 1 value, so it does not allow a combined scope.
            if (scope != StatScope.Global && scope != StatScope.Arena)
                throw new ArgumentException("Only Global or Arena scope are allowed. Combined scopes are not allowed", nameof(scope));

            if (player == null || player[_pdKey] is not PlayerData pd)
            {
                value = default;
                return false;
            }

            if (scope == StatScope.Global)
            {
                return TryGetStat(GetGlobalStatsByInterval(pd, interval), statId, out value);
            }
            else
            {
                return TryGetStat(GetArenaStatsByInterval(pd, interval), statId, out value);
            }

            static bool TryGetStat(SortedDictionary<int, BaseStatInfo> stats, int statId, out TimeSpan value)
            {
                if (stats == null)
                {
                    value = default;
                    return false;
                }

                if (stats.TryGetValue(statId, out BaseStatInfo baseStatInfo)
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
            DateTime now = DateTime.UtcNow;

            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer);

            void DoStartTimer(TimerStatInfo timerStatInfo)
            {
                timerStatInfo.Start(now);
            }
        }

        private void StopTimer(StatScope scope, Player player, int statId, PersistInterval? interval)
        {
            DateTime now = DateTime.UtcNow;

            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer);

            void DoStartTimer(TimerStatInfo timerStatInfo)
            {
                timerStatInfo.Stop(now);
            }
        }

        private void ResetTimer(StatScope scope, Player player, int statId, PersistInterval? interval)
        {
            DoTimerStatOperation(scope, player, statId, interval, DoStartTimer);

            static void DoStartTimer(TimerStatInfo timerStatInfo)
            {
                timerStatInfo.Reset();
            }
        }

        private void GetPersistData(Player player, Stream outStream, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            SortedDictionary<int, BaseStatInfo> stats = state.Scope switch
            {
                PersistScope.PerArena => GetArenaStatsByInterval(pd, state.Interval),
                PersistScope.Global => GetGlobalStatsByInterval(pd, state.Interval),
                _ => null
            };

            if (stats == null)
                return;

            DateTime now = DateTime.UtcNow;

            // serialize stats to outStream
            SSProto.PlayerStats playerStats = new();

            foreach ((int key, BaseStatInfo baseStatInfo) in stats)
            {
                // TODO: add logic for the other integer encodings (based on sign and value), might save some space?
                if (baseStatInfo is StatInfo<int> intStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Int32Value = intStatInfo.Value });
                }
                else if (baseStatInfo is StatInfo<uint> uintStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Uint32Value = uintStatInfo.Value });
                }
                else if (baseStatInfo is StatInfo<long> longStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Int64Value = longStatInfo.Value });
                }
                else if (baseStatInfo is StatInfo<ulong> ulongStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Uint64Value = ulongStatInfo.Value });
                }
                else if (baseStatInfo is StatInfo<DateTime> dateTimeStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(dateTimeStatInfo.Value) });
                }
                else if (baseStatInfo is TimerStatInfo timerStatInfo)
                {
                    playerStats.StatMap.Add(
                        key,
                        new SSProto.StatInfo() { Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(timerStatInfo.GetValueAsOf(now)) });
                }
            }

            try
            {
                playerStats.WriteTo(outStream);
            }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Stats), player, $"Error serializing for interval {state.Interval}. {ex.Message}");
                return;
            }
        }

        private void SetPersistData(Player player, Stream inStream, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            SortedDictionary<int, BaseStatInfo> stats = state.Scope switch
            {
                PersistScope.PerArena => GetArenaStatsByInterval(pd, state.Interval),
                PersistScope.Global => GetGlobalStatsByInterval(pd, state.Interval),
                _ => null
            };

            if (stats == null)
                return;

            // deserialize inStream to stats
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

            foreach ((int key, SSProto.StatInfo pStatInfo) in playerStats.StatMap)
            {
                if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Int32Value)
                {
                    stats[key] = new StatInfo<int>() { Value = pStatInfo.Int32Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Uint32Value)
                {
                    stats[key] = new StatInfo<uint>() { Value = pStatInfo.Uint32Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Int64Value)
                {
                    stats[key] = new StatInfo<long>() { Value = pStatInfo.Int64Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Uint64Value)
                {
                    stats[key] = new StatInfo<ulong>() { Value = pStatInfo.Uint64Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sint32Value)
                {
                    stats[key] = new StatInfo<int>() { Value = pStatInfo.Sint32Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sint64Value)
                {
                    stats[key] = new StatInfo<long>() { Value = pStatInfo.Sint64Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Fixed32Value)
                {
                    stats[key] = new StatInfo<uint>() { Value = pStatInfo.Fixed32Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Fixed64Value)
                {
                    stats[key] = new StatInfo<ulong>() { Value = pStatInfo.Fixed64Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sfixed32Value)
                {
                    stats[key] = new StatInfo<int>() { Value = pStatInfo.Sfixed32Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Sfixed64Value)
                {
                    stats[key] = new StatInfo<long>() { Value = pStatInfo.Sfixed64Value };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Timestamp)
                {
                    stats[key] = new StatInfo<DateTime>() { Value = pStatInfo.Timestamp.ToDateTime() };
                }
                else if (pStatInfo.StatInfoCase == SSProto.StatInfo.StatInfoOneofCase.Duration)
                {
                    stats[key] = new TimerStatInfo(pStatInfo.Duration.ToTimeSpan());
                }
            }
        }

        private void ClearPersistData(Player player, (PersistInterval Interval, PersistScope Scope) state)
        {
            if (player == null || player[_pdKey] is not PlayerData pd)
                return;

            SortedDictionary<int, BaseStatInfo> stats = state.Scope switch
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

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = "[-g] [{forever} | {game} | {reset}]",
            Description = "Prints out some basic statistics about the target player, or if no\n" +
            "target, yourself. By default, it will show arena stats. Use {-g} to switch it to\n" +
            "show global (zone-wide) stats. An interval name can be specified as an argument.\n" +
            "By default, the per-reset interval is used.")]
        private void Command_stats(string commandName, string parameters, Player p, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                targetPlayer = p;
            }

            if (targetPlayer[_pdKey] is not PlayerData pd)
                return;

            PersistScope scope = PersistScope.PerArena;
            PersistInterval interval = PersistInterval.Reset;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                if (token.Equals("-g", StringComparison.OrdinalIgnoreCase))
                {
                    scope = PersistScope.Global;
                }
                else
                {
                    if (!Enum.TryParse(token, true, out interval)
                        || !_intervals.Contains(interval))
                    {
                        _chat.SendMessage(p, $"Invalid interval");
                        return;
                    }                    
                }
            }

            SortedDictionary<int, BaseStatInfo> stats = scope switch
            {
                PersistScope.PerArena => GetArenaStatsByInterval(pd, interval),
                PersistScope.Global => GetGlobalStatsByInterval(pd, interval),
                _ => null,
            };
            
            if (stats == null)
                return;

            _chat.SendMessage(p, $"The server is keeping track of the following {(scope == PersistScope.Global ? "global " : "")}{interval} stats about {(targetPlayer != p ? targetPlayer.Name : "you" )}:");

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                foreach ((int statId, BaseStatInfo baseStatinfo) in stats)
                {
                    string statName = null;
                    GetStatNameCallback.Fire(_broker, statId, ref statName);
                    if (string.IsNullOrWhiteSpace(statName))
                        statName = statId.ToString(CultureInfo.InvariantCulture);

                    if (baseStatinfo is StatInfo<int> intStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {intStatInfo.Value}");
                    }
                    else if (baseStatinfo is StatInfo<uint> uintStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {uintStatInfo.Value}");
                    }
                    else if (baseStatinfo is StatInfo<long> longStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {longStatInfo.Value}");
                    }
                    else if (baseStatinfo is StatInfo<ulong> ulongStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {ulongStatInfo.Value}");
                    }
                    else if (baseStatinfo is StatInfo<DateTime> dateTimeStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {dateTimeStatInfo.Value}");
                    }
                    else if (baseStatinfo is TimerStatInfo timerStatInfo)
                    {
                        _chat.SendMessage(p, $"  {statName}: {timerStatInfo.GetValueAsOf(now)}");
                    }
                }
            }
        }

        private void Callback_PersistIntervalEnded(PersistInterval interval)
        {
            if (interval == PersistInterval.Reset)
            {
                ((IScoreStats)this).SendUpdates(null, null);
            }
        }

        private void Callback_NewPlayer(Player p, bool isNew)
        {
            if (p[_pdKey] is not PlayerData pd)
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
                            pd.CurrentArenaStats.Add(interval, new());
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

        private void Callback_GetStatName(int statId, ref string statName)
        {
            if (!string.IsNullOrWhiteSpace(statName))
                return;

            StatId statCode = (StatId)statId;
            if (!Enum.IsDefined(statCode))
                return;

            statName = statCode switch
            {
                StatId.KillPoints => "kill points",
                StatId.FlagPoints => "flag points",
                StatId.Kills => "kills",
                StatId.Deaths => "deaths",
                StatId.Assists => "assists",
                StatId.TeamKills => "team kills",
                StatId.TeamDeaths => "team deaths",
                StatId.ArenaTotalTime => "total time (this arena)",
                StatId.ArenaSpecTime => "spec time (this arena)",
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

        private static SortedDictionary<int, BaseStatInfo> GetArenaStatsByInterval(PlayerData pd, PersistInterval interval)
        {
            if (pd == null)
                return null;

            if (pd.CurrentArenaStats.TryGetValue(interval, out SortedDictionary<int, BaseStatInfo> stats))
                return stats;
            else
                return null;
        }

        private static SortedDictionary<int, BaseStatInfo> GetGlobalStatsByInterval(PlayerData pd, PersistInterval interval)
        {
            if (pd == null)
                return null;

            if (pd.GlobalStats.TryGetValue(interval, out SortedDictionary<int, BaseStatInfo> stats))
                return stats;
            else
                return null;
        }

        [Flags]
        private enum StatScope
        {
            Global = 1,
            Arena = 2,
            All = 3,
        }

        private class PlayerData
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

            public readonly object Lock = new();
        }

        private abstract class BaseStatInfo
        {
            public bool IsDirty;

            public abstract void Clear();
        }

        private class StatInfo<T> : BaseStatInfo where T : struct, IEquatable<T>
        {
            public T Value { get; set; }

            public override void Clear()
            {
                if (!Value.Equals(default(T)))
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
    }
}
