using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules.Scoring
{
    public class SpeedGame : IModule, IArenaAttachableModule
    {
        private IAllPlayerStats _allPlayerStats;
        private IArenaManager _arenaManager;
        private IArenaPlayerStats _arenaPlayerStats;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private IGame _game;
        private IGameTimer _gameTimer;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private INetwork _network;
        private IPersistExecutor _persistExecutor;
        private IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IAllPlayerStats allPlayerStats,
            IArenaManager arenaManager,
            IArenaPlayerStats arenaPlayerStats,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            IGameTimer gameTimer,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            INetwork network,
            IPersistExecutor persistExecutor,
            IPlayerData playerData)
        {
            _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _arenaPlayerStats = arenaPlayerStats ?? throw new ArgumentNullException(nameof(arenaPlayerStats));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _gameTimer = gameTimer ?? throw new ArgumentNullException(nameof(gameTimer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _persistExecutor = persistExecutor ?? throw new ArgumentNullException(nameof(persistExecutor));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _commandManager.AddCommand("speedstats", Command_speedstats, arena);
            _commandManager.AddCommand("best", Command_best, arena);

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            KillCallback.Register(arena, Callback_Kill);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            KillCallback.Unregister(arena, Callback_Kill);

            _commandManager.RemoveCommand("speedstats", Command_speedstats, arena);
            _commandManager.RemoveCommand("best", Command_best, arena);

            return true;
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

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == PlayerAction.EnterArena)
            {
                UpdateRank(arena, p, false);
            }
            else if (action == PlayerAction.LeaveArena)
            {
                UpdateRank(arena, p, true);
            }

            if (action == PlayerAction.EnterArena || action == PlayerAction.LeaveArena)
            {
                CheckStart(arena);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            CheckStart(player.Arena);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            UpdateRank(arena, killer, false);
        }

        #endregion

        #region Commands

        private void Command_best(string commandName, string parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            if (_arenaPlayerStats.TryGetStat(targetPlayer, StatCodes.SpeedPersonalBest, PersistInterval.Forever, out uint points))
            {
                if (targetPlayer == player)
                    _chat.SendMessage(player, $"Your personal best: {points}");
                else
                    _chat.SendMessage(player, $"{targetPlayer.Name}'s personal best: {points}");
            }
            else
            {
                if (targetPlayer == player)
                    _chat.SendMessage(player, $"You do not have a personal best yet.");
                else
                    _chat.SendMessage(player, $"{targetPlayer.Name} does not have a personal best yet.");
            }
        }

        private void Command_speedstats(string commandName, string parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState != GameState.Running)
            {
                _chat.SendMessage(player, "The game has not yet started.");
                return;
            }

            if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                // Show nearby ranks
                int index = ad.Rank.IndexOf(targetPlayer);
                if (index != -1)
                {
                    int start = index - 2;
                    if (start < 0)
                        start = 0;

                    int end = index + 2;
                    if (end >= ad.Rank.Count)
                        end = ad.Rank.Count - 1;

                    PrintStats(ad, player, start, end);
                    return;
                }
            }

            // Top 5
            PrintStats(ad, player, 0, 4);

            void PrintStats(ArenaData ad, Player player, int start, int end)
            {
                if (ad.Rank.Count == 0)
                    _chat.SendMessage(player, $"No rankings yet.");

                for (int i = start; i <= end && i < ad.Rank.Count; i++)
                {
                    Player otherPlayer = ad.Rank[i];
                    if (!_arenaPlayerStats.TryGetStat(otherPlayer, StatCodes.KillPoints, PersistInterval.Game, out ulong otherKillPoints))
                        continue; // shouldn't happen

                    _chat.SendMessage(player, $"#{i+1} {otherKillPoints,6} {otherPlayer.Name}");
                }
            }
        }

        #endregion

        #region MainloopTimers

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

        private bool MainloopTimer_EndGameTimer(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState != GameState.Running)
                return false;

            EndGame(arena, false, true, true);
            return false;
        }

        #endregion

        private void CheckStart(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState != GameState.Starting)
                return;

            // Make sure we have enough players to 
            int playerCount = 0;

            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena
                        && player.Ship != ShipType.Spec)
                    {
                        playerCount++;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (playerCount < ad.Settings.MinPlayers)
            {
                // Not enough players.
                if (ad.StartAfter != null)
                {
                    _chat.SendArenaMessage(arena, $"Speed game: Not enough players to start.");
                    ad.StartAfter = null; // signal that it can't start

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_StartGameTimer, arena);
                }
            }
            else
            {
                if (ad.StartAfter == null)
                {
                    _chat.SendArenaMessage(arena, $"Speed game: A new round will begin in {ad.Settings.StartDelay.TotalSeconds} seconds.");
                    ad.StartAfter = DateTime.UtcNow + ad.Settings.StartDelay;

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_StartGameTimer, arena);
                    _mainloopTimer.SetTimer(MainloopTimer_StartGameTimer, (int)ad.Settings.StartDelay.TotalMilliseconds, 1000, arena, arena);
                }
            }
        }

        private bool StartGame(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState != GameState.Starting)
                return false;

            // At this point, we're committed to starting. So use an extra state to prevent it from getting cancelled.
            ad.GameState = GameState.StartingInProcess;

            // Speed game stats are based on the points in the current 'Game' interval.
            // Players may have accrued points in the time between speed games, so we need to reset the 'Game' interval.
            // Using EndInterval would work too, but that would save an extra interval to the database for a game that never happened.
            // So instead, we're resetting the interval.

            // The persist methods work asynchronously on a worker thread.
            // Therefore, we're using a callback for when the reset is complete.
            // The callback is called on the mainloop thread.
            _persistExecutor.ResetGameInterval(arena, GameIntervalResetCompleted);
            return true;

            void GameIntervalResetCompleted(Arena arena)
            {
                ad.GameState = GameState.Running;
                ad.Rank.Clear();

                _game.GivePrize(arena, Prize.Warp, 1);
                _game.ShipReset(arena);
                _chat.SendArenaMessage(arena, ChatSound.Beep2, $"Speed game started.");
                _logManager.LogA(LogLevel.Info, nameof(SpeedGame), arena, $"Game started.");

                _gameTimer.SetTimer(arena, ad.Settings.GameDuration);
                _mainloopTimer.SetTimer(MainloopTimer_EndGameTimer, (int)ad.Settings.GameDuration.TotalMilliseconds, 1000, arena, arena);
            }
        }

        private void EndGame(Arena arena, bool clearTimer, bool processStats, bool allowAutoStart)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.GameState == GameState.Running)
            {
                if (clearTimer)
                {
                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_EndGameTimer, arena);
                }

                if (processStats && ad.Rank.Count > 0)
                {
                    S2C_SpeedStats packet = new(false, 0, 0);

                    // Add the Top 5 into the packet, which is the same for all players.
                    for (int i = 0; i < 5 && i < ad.Rank.Count; i++)
                    {
                        Player player = ad.Rank[i];
                        if (!_arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Game, out ulong killPoints))
                            killPoints = 0; // this shouldn't happen

                        packet.SetPlayerScore(i, (short)player.Id, (uint)killPoints);
                    }

                    // Send the packet to each player in the arena, with personal stats.
                    _playerData.Lock();

                    try
                    {
                        foreach (Player player in _playerData.Players)
                        {
                            if (player.Arena != arena)
                                continue;

                            bool isPersonalBest = false;
                            int rank = ad.Rank.IndexOf(player) + 1;
                            uint score = 0;

                            if (rank > 0
                                && _arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Game, out ulong killPoints)
                                && killPoints > 0)
                            {
                                score = (uint)killPoints;

                                bool hasPreviousBest = _arenaPlayerStats.TryGetStat(player, StatCodes.SpeedPersonalBest, PersistInterval.Forever, out uint personalBest);

                                isPersonalBest = !hasPreviousBest || (hasPreviousBest && score >= personalBest);

                                if (!hasPreviousBest || (hasPreviousBest && score > personalBest))
                                {
                                    _arenaPlayerStats.SetStat(player, StatCodes.SpeedPersonalBest, PersistInterval.Forever, score);
                                }
                            }

                            packet.SetPersonalStats(isPersonalBest, (ushort)rank, score);
                            _network.SendToOne(player, ref packet, NetSendFlags.Reliable);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    if (ad.Rank.Count > 0)
                    {
                        Player winner = ad.Rank[0];
                        _allPlayerStats.IncrementStat(winner, StatCodes.SpeedGamesWon, null, 1);
                    }
                }
                else
                {
                    _chat.SendArenaMessage(arena, ChatSound.Ding, "Speed game ended.");
                }
            }

            //
            // Reset game
            //

            _persistExecutor.EndInterval(PersistInterval.Game, arena);

            ad.GameState = GameState.Stopped;
            ad.Rank.Clear();

            if (allowAutoStart && ad.Settings.AutoStart)
            {
                ad.GameState = GameState.Starting;
                ad.StartAfter = null;

                CheckStart(arena);
            }
        }

        private int UpdateRank(Arena arena, Player player, bool remove)
        {
            if (arena == null)
                return -1;

            if (player == null)
                return -1;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return -1;

            if (ad.GameState != GameState.Running)
                return -1;

            if (!_arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Game, out ulong killPoints) || killPoints == 0)
                return -1; // no points yet, not ranked

            int index = ad.Rank.IndexOf(player);

            if (remove)
            {
                if (index != -1)
                    ad.Rank.RemoveAt(index);

                return -1;
            }
            
            if (index == -1)
            {
                // not ranked yet
                for (int i = ad.Rank.Count - 1; i >= 0; i--)
                {
                    Player otherPlayer = ad.Rank[i];
                    if (!_arenaPlayerStats.TryGetStat(otherPlayer, StatCodes.KillPoints, PersistInterval.Game, out ulong otherKillPoints))
                        continue; // this shouldn't happen

                    if (otherKillPoints >= killPoints)
                    {
                        index = i + 1;
                        break;
                    }
                }

                if (index == -1)
                    index = 0;

                ad.Rank.Insert(index, player);
            }
            else
            {
                // Note: This is when a player gains points, so rank can only increase.
                for (int i = index - 1; i >= 0; i--)
                {
                    Player otherPlayer = ad.Rank[i];
                    if (!_arenaPlayerStats.TryGetStat(otherPlayer, StatCodes.KillPoints, PersistInterval.Game, out ulong otherKillPoints))
                        continue; // this shouldn't happen

                    if (killPoints > otherKillPoints)
                    {
                        // move the player up in rank by swapping
                        ad.Rank[i + 1] = otherPlayer;
                        ad.Rank[i] = player;
                        index = i;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (index == 0)
            {
                _chat.SendArenaMessage(arena, $"Speed game: {ad.Rank[0].Name} is in the lead with {killPoints} points!");
            }

            return index;
        }

        #region Helper types

        private struct Settings
        {
            public bool AutoStart;
            public TimeSpan StartDelay;
            public TimeSpan GameDuration;
            public int MinPlayers;

            public Settings(IConfigManager configManager, ConfigHandle ch)
            {
                AutoStart = configManager.GetInt(ch, "Speed", "AutoStart", 1) != 0;
                StartDelay = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "Speed", "StartDelay", 1000) * 10);
                GameDuration = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "Speed", "GameDuration", 3000) * 10);
                MinPlayers = configManager.GetInt(ch, "Speed", "MinPlayers", 2);
            }
        }

        private enum GameState
        {
            Stopped,
            Starting,
            StartingInProcess,
            Running,
        }

        private class ArenaData
        {
            public Settings Settings;
            public GameState GameState = GameState.Stopped;
            public DateTime? StartAfter;

            public readonly List<Player> Rank = new();
        }

        #endregion
    }
}
