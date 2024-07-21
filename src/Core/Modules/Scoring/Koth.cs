using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module that provides functionality for King of the Hill games.
    /// </summary>
    [CoreModuleInfo]
    public class Koth : IModule, IArenaAttachableModule, ICrownsBehavior
    {
        private IAllPlayerStats _allPlayerStats;
        private IArenaManager _arenaManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ICrowns _crowns;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IScoreStats _scoreStats;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IAllPlayerStats allPlayerStats,
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ICrowns crowns,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IScoreStats scoreStats)
        {
            _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _crowns = crowns ?? throw new ArgumentNullException(nameof(crowns));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            KillCallback.Register(arena, Callback_Kill);

            _commandManager.AddCommand("resetkoth", Command_resetkoth, arena);

            ad.ICrownsBehaviorRegistrationToken = arena.RegisterInterface<ICrownsBehavior>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (arena.UnregisterInterface(ref ad.ICrownsBehaviorRegistrationToken) != 0)
                return false;

            _mainloopTimer.ClearTimer<Arena>(MainloopTimer_StartGameTimer, arena);

            _commandManager.RemoveCommand("resetkoth", Command_resetkoth, arena);

            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            KillCallback.Unregister(arena, Callback_Kill);

            return true;
        }

        #endregion

        #region ICrownsBehavior

        void ICrownsBehavior.CrownExpired(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            CheckWinAndExpiration(arena);
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.Settings = new Settings(_configManager, arena.Cfg);

                if (ad.GameState == GameState.Stopped
                    && ad.Settings.AutoStart)
                {
                    ad.GameState = GameState.Starting;
                    ad.StartAfter = null;

                    CheckStart(arena);
                }
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (player.Packet.HasCrown)
            {
                RemoveCrown(player);
                CheckWinAndExpiration(player.Arena);
            }

            CheckStart(player.Arena);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!killer.TryGetExtraData(_pdKey, out PlayerData killerData))
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedData))
                return;

            bool killedHadCrown = killed.Packet.HasCrown;
            bool killedLostCrown = false;

            if (killedHadCrown)
            {
                if (++killedData.Deaths > ad.Settings.DeathCount)
                {
                    RemoveCrown(killed);
                    killedLostCrown = true;
                }
            }

            if (killer.Packet.HasCrown)
            {
                if (killedHadCrown)
                {
                    // killer gets a full timer
                    killerData.ExpireTimestamp = DateTime.UtcNow + ad.Settings.ExpireTimeSpan;
                    _crowns.TrySetTime(killer, ad.Settings.ExpireTimeSpan);
                }
                else if (bounty >= ad.Settings.NonCrownMinimumBounty)
                {
                    // killer gets some time added to their timer
                    DateTime max = DateTime.UtcNow + ad.Settings.ExpireTimeSpan;
                    killerData.ExpireTimestamp = killerData.ExpireTimestamp.Value + ad.Settings.NonCrownAdjustTimeSpan;
                    if (killerData.ExpireTimestamp.Value > max)
                        killedData.ExpireTimestamp = max;

                    _crowns.TryAddTime(killer, ad.Settings.NonCrownAdjustTimeSpan);
                }
            }
            else
            {
                if (killedHadCrown && ad.Settings.CrownRecoverKills > 0)
                {
                    killerData.CrownKills++;

                    int left = ad.Settings.CrownRecoverKills - killerData.CrownKills;
                    if (left <= 0)
                    {
                        // killer earned back their crown
                        killerData.ExpireTimestamp = DateTime.UtcNow + ad.Settings.ExpireTimeSpan;
                        killerData.CrownKills = 0;
                        killerData.Deaths = 0;

                        _crowns.ToggleOn(killer, ad.Settings.ExpireTimeSpan);

                        _chat.SendMessage(killer, $"You earned back a crown.");
                        _logManager.LogP(LogLevel.Drivel, nameof(Koth), killer, $"Earned back a crown.");
                    }
                    else
                    {
                        _chat.SendMessage(killer, $"{left} {(left == 1 ? "kill" : "kills")} left to earn back a crown.");
                    }
                }
            }

            if (killedLostCrown)
            {
                // this is done last, because the killer could have earned back a crown too
                CheckWinAndExpiration(arena);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena || action == PlayerAction.LeaveArena)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                {
                    pd.Deaths = 0;
                    pd.CrownKills = 0;
                }
            }

            if (action == PlayerAction.LeaveArena)
            {
                CheckWinAndExpiration(arena);
            }

            if (action == PlayerAction.EnterArena || action == PlayerAction.LeaveArena)
            {
                CheckStart(arena);
            }
        }

        #endregion

        #region Commands

        private void Command_resetkoth(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ResetGame(player.Arena, true);
        }

        #endregion

        private void ResetGame(Arena arena, bool allowAutoStart)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState == GameState.Running)
            {
                KothEndedCallback.Fire(arena, arena);
            }

            _crowns.ToggleOff(arena);

            ad.GameState = GameState.Stopped;

            if (allowAutoStart && ad.Settings.AutoStart)
            {
                ad.GameState = GameState.Starting;
                ad.StartAfter = null;

                CheckStart(arena);
            }
        }

        private void GetInitialPlayers(Arena arena, HashSet<Player> crownSet, HashSet<Player> noCrownSet)
        {
            if (crownSet == null)
                throw new ArgumentNullException(nameof(crownSet));

            if (noCrownSet == null)
                throw new ArgumentNullException(nameof(noCrownSet));

            _playerData.Lock();

            try
            {
                // Determine which players will get a crown.
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena
                        || player.Status != PlayerState.Playing)
                    {
                        continue;
                    }

                    if (player.Ship == ShipType.Spec
                        || !player.IsStandard)
                    {
                        noCrownSet.Add(player);
                    }
                    else
                    {
                        crownSet.Add(player);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void CheckStart(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState != GameState.Starting)
                return;

            HashSet<Player> crownSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> noCrownSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetInitialPlayers(arena, crownSet, noCrownSet);

                if (crownSet.Count < ad.Settings.MinPlaying)
                {
                    // Not enough players.
                    if (ad.StartAfter != null)
                    {
                        _chat.SendArenaMessage(arena, $"King of the Hill: not enough players to start");
                        ad.StartAfter = null; // signal that it can't start

                        _mainloopTimer.ClearTimer<Arena>(MainloopTimer_StartGameTimer, arena);
                    }
                }
                else
                {
                    if (ad.StartAfter == null)
                    {
                        _chat.SendArenaMessage(arena, $"King of the Hill: a new round will begin in {ad.Settings.StartDelay.TotalSeconds} seconds");
                        ad.StartAfter = DateTime.UtcNow + ad.Settings.StartDelay;

                        _mainloopTimer.ClearTimer<Arena>(MainloopTimer_StartGameTimer, arena);
                        _mainloopTimer.SetTimer(MainloopTimer_StartGameTimer, (int)ad.Settings.StartDelay.TotalMilliseconds, 1000, arena, arena);
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(crownSet);
                _objectPoolManager.PlayerSetPool.Return(noCrownSet);
            }
        }

        private bool MainloopTimer_StartGameTimer(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState != GameState.Starting)
                return false;

            if (ad.StartAfter == null || DateTime.UtcNow < ad.StartAfter)
                return true; // not time to start yet

            if (StartGame(arena))
            {
                // Started.
                return false;
            }

            return true;
        }

        private bool StartGame(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState != GameState.Starting)
                return false;

            HashSet<Player> crownSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> noCrownSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetInitialPlayers(arena, crownSet, noCrownSet);

                if (crownSet.Count < ad.Settings.MinPlaying)
                    return false;

                // There are enough players to start a game.
                ad.InitialPlayerCount = crownSet.Count;
                DateTime expireTimestamp = DateTime.UtcNow + ad.Settings.ExpireTimeSpan;

                // Determine the most efficient way to toggle crowns, such that least amount of data is sent.
                if (crownSet.Count > noCrownSet.Count)
                {
                    _crowns.ToggleOn(arena, ad.Settings.ExpireTimeSpan);
                    _crowns.ToggleOff(noCrownSet);
                }
                else
                {
                    _crowns.ToggleOff(arena);
                    _crowns.ToggleOn(crownSet, ad.Settings.ExpireTimeSpan);
                }

                foreach (Player player in crownSet)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                        continue;

                    pd.ExpireTimestamp = expireTimestamp;
                    pd.Deaths = 0;
                    pd.CrownKills = 0;
                }

                ad.GameState = GameState.Running;

                _chat.SendArenaMessage(arena, ChatSound.Beep2, $"King of the Hill game started.");
                _logManager.LogA(LogLevel.Info, nameof(Koth), arena, $"Game started.");

                KothStartedCallback.Fire(arena, arena, crownSet);

                return true;
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(crownSet);
                _objectPoolManager.PlayerSetPool.Return(noCrownSet);
            }
        }

        private void RemoveCrown(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            pd.ExpireTimestamp = null;
            pd.Deaths = 0;
            pd.CrownKills = 0;

            _crowns.ToggleOff(player);
        }

        private void CheckWinAndExpiration(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState != GameState.Running)
                return;

            DateTime now = DateTime.UtcNow;

            HashSet<Player> crownSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> hadCrownSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> expiredSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                // find the players that have a crown
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Status != PlayerState.Playing
                            || player.Arena != arena)
                        {
                            continue;
                        }

                        if (player.Packet.HasCrown)
                            crownSet.Add(player);
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                do
                {
                    if (crownSet.Count == 0)
                    {
                        // no one has a crown!

                        if (hadCrownSet.Count > 0 && IsOneTeam(hadCrownSet))
                        {
                            // All expired at the same time.
                            // Found the winner(s).
                            RewardPlayers(arena, ad, hadCrownSet);
                        }
                        else
                        {
                            // game over: no winners
                            _chat.SendArenaMessage(arena, ChatSound.Aww, $"King of the Hill: no winner");
                        }

                        ResetGame(arena, true);
                        return;
                    }

                    if (IsOneTeam(crownSet))
                    {
                        // All the players are on the same team.
                        // Found the winner(s).
                        RewardPlayers(arena, ad, crownSet);
                        ResetGame(arena, true);
                        return;
                    }

                    hadCrownSet.Clear();
                    hadCrownSet.UnionWith(crownSet);
                }
                while (ExpireOldest(crownSet, now, expiredSet));

                // No winner yet.

                // Remove crowns of players we found expired.
                foreach (Player player in expiredSet)
                {
                    RemoveCrown(player);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(crownSet);
                _objectPoolManager.PlayerSetPool.Return(hadCrownSet);
                _objectPoolManager.PlayerSetPool.Return(expiredSet);
            }

            bool IsOneTeam(HashSet<Player> players)
            {
                if (players.Count == 0)
                    return false;

                if (players.Count == 1)
                    return true;

                Player p = null;
                foreach (Player player in players)
                {
                    if (p == null)
                        p = player;
                    else if (p.Freq != player.Freq)
                        return false;
                }

                return true;
            }

            bool ExpireOldest(HashSet<Player> players, DateTime now, HashSet<Player> expiredSet)
            {
                DateTime? oldestExpired = null;

                foreach (Player player in players)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                        continue;

                    if (!pd.ExpireTimestamp.HasValue)
                        continue;

                    DateTime expireTimestamp = pd.ExpireTimestamp.Value;
                    if (expireTimestamp <= now
                        && (oldestExpired == null || expireTimestamp < oldestExpired.Value))
                    {
                        oldestExpired = expireTimestamp;
                    }
                }

                if (oldestExpired == null)
                    return false; // none expired

                foreach (Player player in players)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                        continue;

                    if (pd.ExpireTimestamp == oldestExpired) // TODO: maybe just compare up to the centisecond?
                    {
                        expiredSet.Add(player);
                        players.Remove(player);
                    }
                }

                return true;
            }

            void RewardPlayers(Arena arena, ArenaData ad, HashSet<Player> winners)
            {
                if (arena == null || ad == null)
                    return;

                if (winners.Count == 0)
                    return;

                // regular reward formula
                int points = ad.InitialPlayerCount * ad.InitialPlayerCount * ad.Settings.RewardFactor / 1000;
                if (points <= 0)
                    return;

                // jackpot
                IJackpot jackpot = arena.GetInterface<IJackpot>();
                if (jackpot != null)
                {
                    try
                    {
                        points += jackpot.GetJackpot(arena);
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref jackpot);
                    }
                }

                // split
                if (winners.Count > 0 && ad.Settings.SplitPoints)
                {
                    points /= winners.Count;
                }

                ChatSound sound = ChatSound.Ding;
                foreach (Player player in winners)
                {
                    _allPlayerStats.IncrementStat(player, StatCodes.FlagPoints, null, points);
                    _allPlayerStats.IncrementStat(player, StatCodes.KothGamesWon, null, 1);

                    _chat.SendArenaMessage(arena, sound, $"King of the Hill: {player.Name} awarded {points} points");
                    if (sound == ChatSound.Ding)
                        sound = ChatSound.None;

                    _logManager.LogP(LogLevel.Drivel, nameof(Koth), player, "Won KOTH game.");
                }

                _scoreStats.SendUpdates(arena, null);

                KothWonCallback.Fire(arena, arena, winners, points);

                // End the 'game' interval for the arena.
                IPersistExecutor persistExecutor = arena.GetInterface<IPersistExecutor>();
                if (persistExecutor != null)
                {
                    try
                    {
                        persistExecutor.EndInterval(PersistInterval.Game, arena);
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref persistExecutor);
                    }
                }
            }
        }

        #region Helper types

        private struct Settings
        {
            // additional settings
            public bool AutoStart;
            public TimeSpan StartDelay;
            public int MinPlaying;
            public bool SplitPoints;

            // regular settings that subgame supports
            public int RewardFactor;
            public int DeathCount;
            public TimeSpan ExpireTimeSpan;
            public TimeSpan NonCrownAdjustTimeSpan;
            public int NonCrownMinimumBounty;
            public int CrownRecoverKills;

            [ConfigHelp("King", "AutoStart", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether KOTH games will automatically start.")]
            [ConfigHelp("King", "StartDelay", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
                Description = "How long to wait before starting a new round in centiseconds.")]
            [ConfigHelp("King", "MinPlayers", ConfigScope.Arena, typeof(int), DefaultValue = "3",
                Description = "Minimum # of players required for a 'King of the Hill' round to begin.")]
            [ConfigHelp("King", "SplitPoints", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether to split a reward between the winners or give each the full amount.")]
            [ConfigHelp("King", "RewardFactor", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
                Description = "Number of points given to winner of 'King of the Hill' round calculated as (players in arena)^2 * RewardFactor / 1000.")]
            [ConfigHelp("King", "DeathCount", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Number of deaths a player is allowed until his crown is removed.")]
            [ConfigHelp("King", "ExpireTime", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Initial time given to each player at the beginning of a 'King of the Hill' round.")]
            [ConfigHelp("King", "NonCrownAdjustTime", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Amount of time added for killing a player without a crown.")]
            [ConfigHelp("King", "NonCrownMinimumBounty", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Minimum amount of bounty a player must have in order to receive the extra time.")]
            [ConfigHelp("King", "CrownRecoverKills", ConfigScope.Arena, typeof(int), DefaultValue = "3",
                Description = "Number of crown kills a non-crown player must get in order to get their crown back.")]
            public Settings(IConfigManager configManager, ConfigHandle ch)
            {
                AutoStart = configManager.GetInt(ch, "King", "AutoStart", 1) != 0;
                StartDelay = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "King", "StartDelay", 1000) * 10);
                MinPlaying = configManager.GetInt(ch, "King", "MinPlayers", 2);
                SplitPoints = configManager.GetInt(ch, "King", "SplitPoints", 1) != 0;
                RewardFactor = configManager.GetInt(ch, "King", "RewardFactor", 1000);
                DeathCount = configManager.GetInt(ch, "King", "DeathCount", 0);
                ExpireTimeSpan = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "King", "ExpireTime", 0) * 10);
                NonCrownAdjustTimeSpan = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "King", "NonCrownAdjustTime", 0) * 10);
                NonCrownMinimumBounty = configManager.GetInt(ch, "King", "NonCrownMinimumBounty", 0);
                CrownRecoverKills = configManager.GetInt(ch, "King", "CrownRecoverKills", 3);
            }
        }

        private enum GameState
        {
            Stopped,
            Starting,
            Running,
        }

        private class ArenaData : IResettable
        {
            public Settings Settings;
            public InterfaceRegistrationToken<ICrownsBehavior> ICrownsBehaviorRegistrationToken;
            public GameState GameState = GameState.Stopped;
            public DateTime? StartAfter;
            public int InitialPlayerCount;

            public bool TryReset()
            {
                Settings = default;
                ICrownsBehaviorRegistrationToken = null;
                GameState = GameState.Stopped;
                StartAfter = null;
                InitialPlayerCount = 0;
                return true;
            }
        }

        private class PlayerData : IResettable
        {
            public DateTime? ExpireTimestamp;
            public int Deaths;
            public int CrownKills;

            public bool TryReset()
            {
                ExpireTimestamp = null;
                Deaths = 0;
                CrownKills = 0;
                return true;
            }
        }

        #endregion
    }
}
