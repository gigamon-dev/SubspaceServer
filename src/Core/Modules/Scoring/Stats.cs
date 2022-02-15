using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SS.Core.Modules.Scoring
{
    public class Stats : IModule, IStats, IScoreStats
    {
        private ComponentBroker _broker;
        private IChat _chat;
        private ICommandManager _commandManager;
        private INetwork _network;
        //private IPersist _persist;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken _iStatsToken;
        private InterfaceRegistrationToken _iScoreStatsToken;

        private int _pdKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            INetwork network,
            //IPersist persist,
            IPlayerData playerData)
        {
            _broker = broker;
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _network = network ?? throw new ArgumentNullException(nameof(_network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _commandManager.AddCommand("stats", Command_stats);

            // TODO: persist

            // TODO: IntervalEndedCallback.Register(broker, Callback_IntervalEnded)
            NewPlayerCallback.Register(broker, Callback_NewPlayer);
            GetStatNameCallback.Register(broker, Callback_GetStatName); // TODO: this might be nicer on an advisor interface (change this when IAdvisor is added to the ComponentBroker)

            _iStatsToken = broker.RegisterInterface<IStats>(this);
            _iScoreStatsToken = broker.RegisterInterface<IScoreStats>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IStats>(ref _iStatsToken);
            broker.UnregisterInterface<IScoreStats>(ref _iScoreStatsToken);

            // TODO: IntervalEndedCallback.Unregister(broker, Callback_IntervalEnded)
            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);

            // TODO: persist

            _commandManager.RemoveCommand("stats", Command_stats);

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region IStats members

        void IStats.IncrementStat(Player p, int statId, int amount)
        {
            if (p[_pdKey] is not PlayerData pd)
                return;

            lock (pd.Lock)
            {
                Inc(pd.Forever, statId, amount);
                Inc(pd.Reset, statId, amount);
                Inc(pd.Game, statId, amount);
            }

            static void Inc(SortedDictionary<int, StatInfo> stats, int statId, int amount)
            {
                if (!stats.TryGetValue(statId, out StatInfo statInfo))
                {
                    statInfo = new StatInfo();
                    stats.Add(statId, statInfo);
                }

                statInfo.Value += amount;
                statInfo.IsDirty = true;
            }
        }

        void IStats.StartTimer(Player p, int statId)
        {
            if (p[_pdKey] is not PlayerData pd)
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                StartTimer(pd.Forever, statId, now);
                StartTimer(pd.Reset, statId, now);
                StartTimer(pd.Game, statId, now);
            }

            static void StartTimer(SortedDictionary<int, StatInfo> stats, int statId, DateTime time)
            {
                if (!stats.TryGetValue(statId, out StatInfo statInfo))
                {
                    statInfo = new StatInfo();
                    stats.Add(statId, statInfo);
                }

                if (statInfo.Started != null)
                {
                    UpdateTimer(statInfo, time);
                }
                else
                {
                    statInfo.Started = time;
                }
            }
        }

        void IStats.StopTimer(Player p, int statId)
        {
            if (p[_pdKey] is not PlayerData pd)
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                StopTimer(pd.Forever, statId, now);
                StopTimer(pd.Reset, statId, now);
                StopTimer(pd.Game, statId, now);
            }

            static void StopTimer(SortedDictionary<int, StatInfo> stats, int statId, DateTime time)
            {
                if (!stats.TryGetValue(statId, out StatInfo statInfo))
                {
                    statInfo = new StatInfo();
                    stats.Add(statId, statInfo);
                }

                UpdateTimer(statInfo, time);
                statInfo.Started = null;
            }
        }

        void IStats.SetStat(Player p, int statId, StatInterval interval, int value)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            var stats = GetStatsByInterval(pd, interval);
            if (stats == null)
                return;

            if (!stats.TryGetValue(statId, out StatInfo statInfo))
            {
                statInfo = new StatInfo();
                stats.Add(statId, statInfo);
            }

            statInfo.Value = value;
            statInfo.Started = null; // setting a stat stops any timers that were running
            statInfo.IsDirty = true;
        }

        bool IStats.TryGetStat(Player p, int statId, StatInterval interval, out int value)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
            {
                value = default;
                return false;
            }

            var stats = GetStatsByInterval(pd, interval);
            if (stats == null)
            {
                value = default;
                return false;
            }

            if (!stats.TryGetValue(statId, out StatInfo statInfo))
            {
                value = default;
                return false;
            }

            value = statInfo.Value;
            return true;
        }

        private static void UpdateTimer(StatInfo statInfo, DateTime time)
        {
            if (statInfo.Started != null)
            {
                statInfo.Value += (int)(time - statInfo.Started.Value).TotalSeconds;
                statInfo.Started = time;
                statInfo.IsDirty = true;
            }
        }

        #endregion

        #region IScoreStats

        public void SendUpdates(Arena arena, Player exclude)
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
                            GetStatValueAndCheckDirtyInt32(pd, (int)StatCode.KillPoints, ref killPoints, ref isDirty);
                            GetStatValueAndCheckDirtyInt32(pd, (int)StatCode.FlagPoints, ref flagPoints, ref isDirty);
                            GetStatValueAndCheckDirtyInt16(pd, (int)StatCode.Kills, ref kills, ref isDirty);
                            GetStatValueAndCheckDirtyInt16(pd, (int)StatCode.Deaths, ref deaths, ref isDirty);
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

            static void GetStatValueAndCheckDirtyInt32(PlayerData pd, int statCode, ref int value, ref bool isDirty)
            {
                if (pd.Reset.TryGetValue(statCode, out StatInfo statInfo))
                {
                    value = statInfo.Value;

                    if (statInfo.IsDirty)
                    {
                        isDirty = true;
                        statInfo.IsDirty = false;
                    }
                }
            }

            static void GetStatValueAndCheckDirtyInt16(PlayerData pd, int statCode, ref short value, ref bool isDirty)
            {
                if (pd.Reset.TryGetValue(statCode, out StatInfo statInfo))
                {
                    value = (short)statInfo.Value;

                    if (statInfo.IsDirty)
                    {
                        isDirty = true;
                        statInfo.IsDirty = false;
                    }
                }
            }
        }

        public void ScoreReset(Player p, StatInterval interval)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            var stats = GetStatsByInterval(pd, interval);
            if (stats == null)
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                foreach (StatInfo statInfo in stats.Values)
                {
                    // Keep timers running.
                    // If the timer was running while this happens, only the time frrom this point will be counted.
                    // The time from the timer start to this point will be discarded.
                    UpdateTimer(statInfo, now);
                    statInfo.Value = 0;
                    statInfo.IsDirty = true;
                }
            }
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = "[{forever} | {game} | {reset}]",
            Description = "Prints out some basic statistics about the target player, or if no\n" +
            "target, yourself. An interval name can be specified as an argument.\n" +
            "By default, the per-reset interval is used.")]
        private void Command_stats(string commandName, string parameters, Player p, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                targetPlayer = p;
            }

            if (targetPlayer[_pdKey] is not PlayerData pd)
                return;

            SortedDictionary<int, StatInfo> stats;

            if (parameters.Contains("forever"))
                stats = pd.Forever;
            else if (parameters.Contains("game"))
                stats = pd.Game;
            else
                stats = pd.Reset;

            _chat.SendMessage(p, $"The server is keeping track of the following stats about {(targetPlayer != p ? targetPlayer.Name : "you" )}:");

            lock (pd.Lock)
            {
                foreach (KeyValuePair<int, StatInfo> kvp in stats)
                {
                    int statId = kvp.Key;

                    string statName = null;
                    GetStatNameCallback.Fire(_broker, statId, ref statName);
                    if (string.IsNullOrWhiteSpace(statName))
                        statName = statId.ToString(CultureInfo.InvariantCulture);

                    _chat.SendMessage(p, $"  {statName}: {kvp.Value.Value}");
                }
            }
        }

        private void Callback_NewPlayer(Player p, bool isNew)
        {
            if (p[_pdKey] is not PlayerData pd)
                return;

            lock (pd.Lock)
            {
                pd.Forever.Clear();
                pd.Reset.Clear();
                pd.Game.Clear();
            }
        }

        private void Callback_GetStatName(int statId, ref string statName)
        {
            if (!string.IsNullOrWhiteSpace(statName))
                return;

            StatCode statCode = (StatCode)statId;
            if (!Enum.IsDefined(statCode))
                return;

            statName = statCode switch
            {
                StatCode.KillPoints => "kill points",
                StatCode.FlagPoints => "flag points",
                StatCode.Kills => "kills",
                StatCode.Deaths => "deaths",
                StatCode.Assists => "assists",
                StatCode.TeamKills => "team kills",
                StatCode.TeamDeaths => "team deaths",
                StatCode.ArenaTotalTime => "total time (this arena)",
                StatCode.ArenaSpecTime => "spec time (this arena)",
                StatCode.DamageTaken => "damage taken",
                StatCode.DamageDealt => "damage dealt",
                StatCode.FlagPickups => "flag pickups",
                StatCode.FlagCarryTime => "flag time",
                StatCode.FlagDrops => "flag drops",
                StatCode.FlagNeutDrops => "flag neutral drops",
                StatCode.FlagKills => "flag kills",
                StatCode.FlagDeaths => "flag deaths",
                StatCode.FlagGamesWon => "flag games won",
                StatCode.FlagGamesLost => "flag games lost",
                StatCode.TurfTags => "turf flag tags",
                StatCode.BallCarries => "ball carries",
                StatCode.BallCarryTime => "ball time",
                StatCode.BallGoals => "goals",
                StatCode.BallGamesWon => "ball games won",
                StatCode.BallGamesLost => "ball games lost",
                StatCode.KothGamesWon => "koth games won",
                StatCode.BallAssists => "assists",
                StatCode.BallSteals => "steals",
                StatCode.BallDelayedSteals => "delayed steals",
                StatCode.BallTurnovers => "turnovers",
                StatCode.BallDelayedTurnovers => "delayed turnovers",
                StatCode.BallSaves => "saves",
                StatCode.BallChokes => "chokes",
                StatCode.BallKills => "kills",
                StatCode.BallTeamKills => "team kills",
                StatCode.BallSpawns => "ball spawns",
                _ => null,
            };
        }

        private static SortedDictionary<int, StatInfo> GetStatsByInterval(PlayerData pd, StatInterval interval)
        {
            if (pd == null)
                return null;

            return interval switch
            {
                StatInterval.Forever => pd.Forever,
                StatInterval.Reset => pd.Reset,
                StatInterval.Game => pd.Game,
                _ => null,
            };
        }

        private class PlayerData
        {
            public readonly SortedDictionary<int, StatInfo> Forever = new();
            public readonly SortedDictionary<int, StatInfo> Reset = new();
            public readonly SortedDictionary<int, StatInfo> Game = new();

            public readonly object Lock = new();
        }

        private class StatInfo
        {
            public int Value;
            public DateTime? Started;
            public bool IsDirty;
        }
    }
}
