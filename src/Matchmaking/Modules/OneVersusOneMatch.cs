﻿using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.Queues;
using SS.Packets.Game;
using System.Diagnostics.CodeAnalysis;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages 1v1 matchmaking.
    /// </summary>
    [ModuleInfo($"""
        Manages 1v1 matchmaking.
        Configuration: {nameof(OneVersusOneMatch)}.conf
        """)]
    public class OneVersusOneMatch : IAsyncModule, IMatchmakingQueueAdvisor
    {
        private const string ConfigurationFileName = "1v1Versus.conf";

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMatchmakingQueues _matchmakingQueues;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private PlayerDataKey<PlayerData> _pdKey;
        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor>? _iMatchmakingQueueAdvisorToken;

        /// <summary>
        /// The base name for the arenas that matches are to be played in.
        /// </summary>
        private string? _arenaBaseName;

        /// <summary>
        /// The maximum # of arenas to use for matches.
        /// </summary>
        private int _maxArenas;

        /// <summary>
        /// Dictionary of queues (key = queue name).
        /// </summary>
        private readonly Dictionary<string, OneVersusOneMatchmakingQueue> _queueDictionary = new(32);

        /// <summary>
        /// Dictionary of boxes for each queue. (key = queue name)
        /// </summary>
        private readonly Dictionary<string, List<int>> _queueBoxes = new(32);

        /// <summary>
        /// Configuration for each box.
        /// </summary>
        private BoxConfiguration[]? _boxConfigs;

        /// <summary>
        /// Dictionary of arena data (key = arena number)
        /// </summary>
        /// <remarks>
        /// Not using per-arena data since it will have data for arenas that aren't created yet.
        /// Also, it only has data for arenas having the base arena name (hence why the key is the arena number).
        /// </remarks>
        private readonly Dictionary<int, ArenaData> _arenaDataDictionary = [];

        public OneVersusOneMatch(
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IMatchmakingQueues matchmakingQueues,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _matchmakingQueues = matchmakingQueues ?? throw new ArgumentNullException(nameof(matchmakingQueues));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!await LoadConfigurationAsync().ConfigureAwait(false))
            {
                return false;
            }

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Register(broker, Callback_MatchmakingQueueChanged);

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken))
                return Task.FromResult(false);

            _playerData.FreePlayerData(ref _pdKey);

            if (_queueDictionary.Count > 0)
            {
                foreach (var queue in _queueDictionary.Values)
                {
                    _matchmakingQueues.UnregisterQueue(queue);
                }
                _queueDictionary.Clear();
            }

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            return Task.FromResult(true);
        }

        #endregion

        #region IMatchmakingQueueAdvisor

        string? IMatchmakingQueueAdvisor.GetDefaultQueue(Arena arena)
        {
            if (string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase)
                && _boxConfigs is not null
                && _boxConfigs.Length > 0)
            {
                return _boxConfigs[0].Queue.Name;
            }

            return null;
        }

        string? IMatchmakingQueueAdvisor.GetQueueNameByAlias(Arena arena, string alias)
        {
            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase)
                || _boxConfigs is null
                || !int.TryParse(alias, out int boxNumber))
            {
                return null;
            }

            boxNumber -= 1; // zero-indexed

            if (boxNumber >= 0 && boxNumber < _boxConfigs.Length)
            {
                return _boxConfigs[boxNumber].Queue.Name;
            }

            return null;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!string.Equals(_arenaBaseName, arena.BaseName, StringComparison.OrdinalIgnoreCase))
                return;

            if (action == ArenaAction.Create)
            {
                KillCallback.Register(arena, Callback_Kill);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
                PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            }
            else if (action == ArenaAction.Destroy)
            {
                KillCallback.Unregister(arena, Callback_Kill);
                ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
                PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);

                // Immediately end any ongoing matches in the arena.
                if (_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                {
                    foreach (BoxState boxState in arenaData.Boxes)
                    {
                        if (boxState.Status == BoxStatus.Playing)
                        {
                            EndMatch(arena, arena.Number, boxState, OneVersusOneMatchEndReason.Aborted, null);
                        }
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterGame)
            {
                if (arena is null
                    || !string.Equals(arena.BaseName, _arenaBaseName)
                    || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                {
                    return;
                }

                playerData.HasEnteredArena = true;

                if (playerData.MatchIdentifier is not null
                    && playerData.MatchIdentifier.ArenaNumber == arena.Number)
                {
                    if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                        return;

                    BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];

                    if (boxState.Status == BoxStatus.Starting)
                    {
                        if (boxState.Player1 == player && boxState.Player1State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player1State = PlayerMatchmakingState.Waiting;
                            QueueMatchInitialization(boxState.MatchIdentifier);
                        }
                        else if (boxState.Player2 == player && boxState.Player2State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player2State = PlayerMatchmakingState.Waiting;
                            QueueMatchInitialization(boxState.MatchIdentifier);
                        }
                    }
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (arena is null || !string.Equals(arena.BaseName, _arenaBaseName))
                    return;

                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                playerData.HasEnteredArena = false;

                if (playerData.MatchIdentifier is not null
                    && arena.Number == playerData.MatchIdentifier.ArenaNumber)
                {
                    if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                        return;

                    BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];
                    if (boxState.Status == BoxStatus.Playing)
                    {
                        if (boxState.Player1 == player)
                        {
                            if (boxState.Player1State == PlayerMatchmakingState.Playing)
                            {
                                boxState.Player1State = PlayerMatchmakingState.GaveUp;
                            }

                            QueueMatchCompletionCheck(boxState.MatchIdentifier);
                            _matchmakingQueues.UnsetPlaying(player, false);
                        }
                        else if (boxState.Player2 == player)
                        {
                            if (boxState.Player2State == PlayerMatchmakingState.Playing)
                            {
                                boxState.Player2State = PlayerMatchmakingState.GaveUp;
                            }

                            QueueMatchCompletionCheck(boxState.MatchIdentifier);
                            _matchmakingQueues.UnsetPlaying(player, false);
                        }
                    }
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                if (playerData.MatchIdentifier is not null)
                {
                    if (!_arenaDataDictionary.TryGetValue(playerData.MatchIdentifier.ArenaNumber, out ArenaData? arenaData))
                        return;

                    // Get rid of references to the player.
                    BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];
                    if (boxState.Player1 == player)
                    {
                        boxState.Player1 = null;
                    }
                    else if (boxState.Player2 == player)
                    {
                        boxState.Player2 = null;
                    }

                    // Note: Don't need to queue a workitem check for match completion since it would have already been done for PlayerAction.LeaveArena
                }
            }
        }

        private void Callback_MatchmakingQueueChanged(IMatchmakingQueue queue, QueueAction action)
        {
            if (action != QueueAction.Add
                || !_queueDictionary.TryGetValue(queue.Name, out OneVersusOneMatchmakingQueue? found)
                || found != queue)
            {
                return;
            }

            // Match until no more matches can be made.
            while (DoMatching(found)) { }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Packets.Game.Prize green)
        {
            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!killer.TryGetExtraData(_pdKey, out PlayerData? killerPlayerData) || killerPlayerData.MatchIdentifier is null)
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData? killedPlayerData) || killedPlayerData.MatchIdentifier is null || killerPlayerData.MatchIdentifier != killedPlayerData.MatchIdentifier)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                return;

            BoxState boxState = arenaData.Boxes[killedPlayerData.MatchIdentifier.BoxId];
            if (boxState.Status == BoxStatus.Playing)
            {
                if (boxState.Player1 == killer && boxState.Player2 == killed)
                {
                    boxState.Player2State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier, 2000); // TODO: make the delay a setting
                }
                else if (boxState.Player2 == killer && boxState.Player1 == killed)
                {
                    boxState.Player1State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier, 2000); // TODO: make the delay a setting
                }
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData) || playerData.MatchIdentifier is null)
                return;

            int boxId = playerData.MatchIdentifier.BoxId;
            BoxState boxState = arenaData.Boxes[boxId];
            if (boxState.Status == BoxStatus.Playing)
            {
                BoxConfiguration boxConfig = _boxConfigs![boxId];

                if (newShip == ShipType.Spec)
                {
                    if (boxState.Player1 == player && boxState.Player1State == PlayerMatchmakingState.Playing)
                    {
                        boxState.Player1State = PlayerMatchmakingState.GaveUp;
                        QueueMatchCompletionCheck(boxState.MatchIdentifier);
                        _matchmakingQueues.UnsetPlaying(player, false);
                    }
                    else if (boxState.Player2 == player && boxState.Player2State == PlayerMatchmakingState.Playing)
                    {
                        boxState.Player2State = PlayerMatchmakingState.GaveUp;
                        QueueMatchCompletionCheck(boxState.MatchIdentifier);
                        _matchmakingQueues.UnsetPlaying(player, false);
                    }
                }
                else
                {
                    if (boxState.Player1 == player)
                    {
                        _game.WarpTo(player, boxConfig.StartLocation1.X, boxConfig.StartLocation1.Y);
                    }
                    else if (boxState.Player2 == player)
                    {
                        _game.WarpTo(player, boxConfig.StartLocation2.X, boxConfig.StartLocation2.Y);
                    }

                    playerData.LastShip = newShip;
                }
            }
        }

        private void Callback_PlayerPositionPacket(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if ((positionPacket.Status & PlayerPositionStatus.Safezone) == 0)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData? arenaData))
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData) || playerData.MatchIdentifier is null)
                return;

            BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];
            if (boxState.Status == BoxStatus.Playing)
            {
                if (boxState.Player1 == player)
                {
                    boxState.Player1State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier);
                }
                else if (boxState.Player2 == player)
                {
                    boxState.Player2State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier);
                }
            }
        }

        #endregion

        private async Task<bool> LoadConfigurationAsync()
        {
            ConfigHandle? ch = await _configManager.OpenConfigFileAsync(null, ConfigurationFileName).ConfigureAwait(false);
            if (ch is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(OneVersusOneMatch), $"Error opening {ConfigurationFileName}.");
                return false;
            }

            try
            {
                _arenaBaseName = _configManager.GetStr(ch, "Matchmaking", "ArenaBaseName");
                _maxArenas = _configManager.GetInt(ch, "Matchmaking", "MaxArenas", 10);
                if (_maxArenas < 1)
                    _maxArenas = 1;

                int boxCount = _configManager.GetInt(ch, "Matchmaking", "Boxes", 0);
                _boxConfigs = new BoxConfiguration[boxCount];

                for (int i = 0; i < _boxConfigs.Length; i++)
                {
                    int box = i + 1;

                    string? queueName = _configManager.GetStr(ch, $"Box{box}", "Queue");
                    if (string.IsNullOrWhiteSpace(queueName))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(OneVersusOneMatch), $"Invalid Queue for Box{box}.");
                        return false;
                    }

                    string? coordinateStr = _configManager.GetStr(ch, $"Box{box}", "StartPlayer1");
                    if (!TileCoordinates.TryParse(coordinateStr, out TileCoordinates startLocation1))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(OneVersusOneMatch), $"Invalid StartPlayer1 for Box{box}.");
                        return false;
                    }

                    coordinateStr = _configManager.GetStr(ch, $"Box{box}", "StartPlayer2");
                    if (!TileCoordinates.TryParse(coordinateStr, out TileCoordinates startLocation2))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(OneVersusOneMatch), $"Invalid StartPlayer2 for Box{box}.");
                        return false;
                    }

                    if (!_queueDictionary.TryGetValue(queueName, out OneVersusOneMatchmakingQueue? queue))
                    {
                        string? description = _configManager.GetStr(ch, $"Queue-{queueName}", "Description");
                        bool allowAutoRequeue = _configManager.GetInt(ch, $"Queue-{queueName}", "AllowAutoRequeue", 0) != 0;

                        queue = new OneVersusOneMatchmakingQueue(
                            queueName,
                            new QueueOptions
                            {
                                AllowSolo = true,
                                AllowGroups = false,
                                AllowAutoRequeue = true,
                            },
                            description);

                        if (!_matchmakingQueues.RegisterQueue(queue))
                        {
                            _logManager.LogM(LogLevel.Error, nameof(OneVersusOneMatch), $"Failed to register queue '{queueName}' (used by Box{box}).");
                            return false;
                        }

                        _queueDictionary.Add(queueName, queue);
                    }

                    if (!_queueBoxes.TryGetValue(queueName, out List<int>? boxList))
                    {
                        boxList = new List<int>(1);
                        _queueBoxes.Add(queueName, boxList);
                    }

                    boxList.Add(i);

                    _boxConfigs[i] = new BoxConfiguration(queue, startLocation1, startLocation2);
                }
            }
            finally
            {
                _configManager.CloseConfigFile(ch);
            }

            return true;
        }

        private bool DoMatching(OneVersusOneMatchmakingQueue queue)
        {
            if (queue is null)
                return false;

            // Find an available spot for the match..
            if (!TryGetAvailableArenaAndBox(queue, out int arenaNumber, out int boxId, out ArenaData? arenaData))
            {
                return false;
            }

            Span<char> arenaName = stackalloc char[Constants.MaxArenaNameLength];
            if (!Arena.TryCreateArenaName(arenaName, _arenaBaseName, arenaNumber, out int charsWritten))
                return false;

            arenaName = arenaName[..charsWritten];

            // Check if there are 2 players available.
            if (!queue.GetParticipants(out Player? player1, out Player? player2))
                return false;

            if (!player1.TryGetExtraData(_pdKey, out PlayerData? player1Data)
                || !player2.TryGetExtraData(_pdKey, out PlayerData? player2Data))
            {
                // This should never happen.
                queue.Add(player1, DateTime.MinValue);
                queue.Add(player2, DateTime.MinValue);
                return false;
            }

            // Reserve the spot for the match.
            BoxState boxState = arenaData.Boxes[boxId];
            boxState.Reserve(player1, player2);
            player1Data.MatchIdentifier = player2Data.MatchIdentifier = boxState.MatchIdentifier;

            // Tell the MatchmakingQueues module that the players are playing, so that they are removed from any other queues and prevented from queuing up while playing.
            // Mark the players as 'Playing'.
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                set.Add(player1);
                set.Add(player2);

                _matchmakingQueues.SetPlaying(set);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }

            // Get the players into the correct arena if they aren't already there.
            // TODO: If the player is not in spec, then notify them of the match and which arena to go to and give them 30 seconds to get there.
            Arena? arena = _arenaManager.FindArena(arenaName); // This will only find the arena if it already exists and is running.

            if (arena is not null && player1.Arena == arena)
            {
                boxState.Player1State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player1State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player1, arenaName, 0, 0);
            }

            if (arena is not null && player2.Arena == arena)
            {
                boxState.Player2State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player2State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player2, arenaName, 0, 0);
            }

            QueueMatchInitialization(boxState.MatchIdentifier);
            return true;

            bool TryGetAvailableArenaAndBox(OneVersusOneMatchmakingQueue queue, out int arenaNumber, out int boxId, [MaybeNullWhen(false)] out ArenaData arenaData)
            {
                arenaNumber = 0;

                do
                {
                    // arena data
                    if (!_arenaDataDictionary.TryGetValue(arenaNumber, out arenaData))
                    {
                        arenaData = new ArenaData(arenaNumber, _boxConfigs!.Length);
                        _arenaDataDictionary.Add(arenaNumber, arenaData);
                    }

                    // box
                    if (TryGetAvailableBox(arenaData, queue, out boxId))
                    {
                        return true;
                    }

                    arenaNumber++;
                }
                while (arenaNumber < _maxArenas);

                // no availability
                arenaNumber = 0;
                arenaData = null;
                boxId = 0;
                return false;
            }

            bool TryGetAvailableBox(ArenaData arenaData, OneVersusOneMatchmakingQueue queue, out int boxId)
            {
                if (_queueBoxes.TryGetValue(queue.Name, out List<int>? boxList))
                {
                    foreach (int id in boxList)
                    {
                        if (arenaData.Boxes[id].Status == BoxStatus.None)
                        {
                            boxId = id;
                            return true;
                        }
                    }
                }

                boxId = default;
                return false;
            }
        }

        private void QueueMatchInitialization(MatchIdentifier matchIdentifier)
        {
            _mainloop.QueueMainWorkItem(DoMatchInitialization, matchIdentifier);

            void DoMatchInitialization(MatchIdentifier matchWorkItem)
            {
                Span<char> arenaName = stackalloc char[Constants.MaxArenaNameLength];
                if (!Arena.TryCreateArenaName(arenaName, _arenaBaseName, matchIdentifier.ArenaNumber, out int charsWritten))
                    return;

                arenaName = arenaName[..charsWritten];

                Arena? arena = _arenaManager.FindArena(arenaName);
                if (arena is null)
                    return;

                if (!_arenaDataDictionary.TryGetValue(matchWorkItem.ArenaNumber, out ArenaData? arenaData))
                    return;

                int boxId = matchWorkItem.BoxId;
                BoxState boxState = arenaData.Boxes[boxId];

                if (boxState.Status == BoxStatus.Starting)
                {
                    Player? player1 = boxState.Player1;
                    if (player1 is null || !player1.TryGetExtraData(_pdKey, out PlayerData? player1Data))
                        return;

                    Player? player2 = boxState.Player2;
                    if (player2 is null || !player2.TryGetExtraData(_pdKey, out PlayerData? player2Data))
                        return;

                    if (boxState.Player1State == PlayerMatchmakingState.Waiting
                        && player1Data.HasEnteredArena
                        && boxState.Player2State == PlayerMatchmakingState.Waiting
                        && player2Data.HasEnteredArena)
                    {
                        // The players are in the correct arena. Start the match.
                        short freq1 = (short)(boxId * 2);
                        short freq2 = (short)(boxId * 2 + 1);

                        // Spawn the players (this is automatically in the center).
                        SetShipAndFreq(player1, player1Data, freq1);
                        SetShipAndFreq(player2, player2Data, freq2);

                        // Warp the players to their starting locations.
                        _game.WarpTo(player1, _boxConfigs![boxId].StartLocation1.X, _boxConfigs[boxId].StartLocation1.Y);
                        _game.WarpTo(player2, _boxConfigs[boxId].StartLocation2.X, _boxConfigs[boxId].StartLocation2.Y);

                        // Reset their ships.
                        _game.ShipReset(player1);
                        _game.ShipReset(player2);

                        boxState.Status = BoxStatus.Playing;
                        boxState.Player1State = boxState.Player2State = PlayerMatchmakingState.Playing;

                        OneVersusOneMatchStartedCallback.Fire(arena, arena, matchIdentifier.BoxId, player1, player2);
                    }
                }

                void SetShipAndFreq(Player player, PlayerData playerData, short freq)
                {
                    ShipType ship;
                    if (playerData.LastShip is not null)
                        ship = playerData.LastShip.Value;
                    else
                        playerData.LastShip = ship = ShipType.Warbird;

                    _game.SetShipAndFreq(player, ship, freq);
                }
            }
        }

        private void QueueMatchCompletionCheck(MatchIdentifier matchIdentifier, int delay = 0)
        {
            _mainloopTimer.ClearTimer<MatchIdentifier>(CheckMatchCompletion, matchIdentifier);
            _mainloopTimer.SetTimer(CheckMatchCompletion, delay, Timeout.Infinite, matchIdentifier, matchIdentifier);

            bool CheckMatchCompletion(MatchIdentifier matchIdentifier)
            {
                Span<char> arenaName = stackalloc char[Constants.MaxArenaNameLength];
                if (!Arena.TryCreateArenaName(arenaName, _arenaBaseName, matchIdentifier.ArenaNumber, out int charsWritten))
                    return false;

                arenaName = arenaName[..charsWritten];

                Arena? arena = _arenaManager.FindArena(arenaName);
                if (arena is null)
                    return false;

                if (!_arenaDataDictionary.TryGetValue(matchIdentifier.ArenaNumber, out ArenaData? arenaData))
                    return false;

                BoxState boxState = arenaData.Boxes[matchIdentifier.BoxId];
                if (boxState.Status != BoxStatus.Playing)
                    return false;

                if (boxState.Player1State == PlayerMatchmakingState.KnockedOut
                    && boxState.Player2State == PlayerMatchmakingState.Playing)
                {
                    // Player 2 Wins!
                    EndMatch(arena, matchIdentifier.BoxId, boxState, OneVersusOneMatchEndReason.Decided, 2);
                }
                else if (boxState.Player1State == PlayerMatchmakingState.Playing
                    && boxState.Player2State == PlayerMatchmakingState.KnockedOut)
                {
                    // Player 1 Wins!
                    EndMatch(arena, matchIdentifier.BoxId, boxState, OneVersusOneMatchEndReason.Decided, 1);
                }
                else if (boxState.Player1State == PlayerMatchmakingState.KnockedOut
                    || boxState.Player2State == PlayerMatchmakingState.KnockedOut)
                {
                    // Double knockout, draw
                    // TODO: restart match instead
                    EndMatch(arena, matchIdentifier.BoxId, boxState, OneVersusOneMatchEndReason.Draw, null);
                }
                else if (boxState.Player1State == PlayerMatchmakingState.GaveUp || boxState.Player2State == PlayerMatchmakingState.GaveUp)
                {
                    // Abort the match
                    // NOTE: If the player disconnected, then the player is null. Therefore, all these null checks.
                    if (boxState.Player1State == PlayerMatchmakingState.GaveUp)
                    {
                        if (boxState.Player1 is not null)
                            _chat.SendMessage(boxState.Player1, "You left the match.");

                        if (boxState.Player2 is not null)
                            _chat.SendMessage(boxState.Player2, "Your opponent left the match.");
                    }

                    if (boxState.Player2State == PlayerMatchmakingState.GaveUp)
                    {
                        if (boxState.Player2 is not null)
                            _chat.SendMessage(boxState.Player2, "You left the match.");

                        if (boxState.Player1 is not null)
                            _chat.SendMessage(boxState.Player1, "Your opponent left the match.");
                    }

                    EndMatch(arena, matchIdentifier.BoxId, boxState, OneVersusOneMatchEndReason.Aborted, null);
                }

                return false;
            }
        }

        private void EndMatch(Arena arena, int boxId, BoxState boxState, OneVersusOneMatchEndReason reason, int? winner)
        {
            string? winnerPlayerName = winner switch
            {
                1 => boxState.Player1Name,
                2 => boxState.Player2Name,
                _ => null
            };

            OneVersusOneMatchEndedCallback.Fire(arena, arena, boxId, reason, winnerPlayerName);

            Player? player1 = boxState.Player1;
            Player? player2 = boxState.Player2;

            // Clear match info.
            boxState.Reset();

            if (player1 is not null)
            {
                RemoveFromPlay(player1);
            }

            if (player2 is not null)
            {
                RemoveFromPlay(player2);
            }

            void RemoveFromPlay(Player player)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                playerData.MatchIdentifier = null;

                // Change to spectator mode.
                // NOTE: The ShipFreqChangeCallback gets called asynchronously as a mainloop workitem.
                if (player.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);

                // Remove the players' 'Playing' state with allowed requeuing.
                // Since this can requeue, it will fire the MatchmakingQueueChangedCallback for any that are requeued.
                // However, this must definitely happen after the ShipFreqChangeCallback(s).
                // So, queue this as a mainloop workitem too, which will happen after the ShipFreqChangeCallback(s) occur.
                _mainloop.QueueMainWorkItem(DoUnsetPlaying, player);
            }

            void DoUnsetPlaying(Player player)
            {
                _matchmakingQueues.UnsetPlaying(player, true);
            }
        }

        private readonly struct BoxConfiguration(OneVersusOneMatchmakingQueue queue, TileCoordinates startLocation1, TileCoordinates startLocation2)
        {
            public OneVersusOneMatchmakingQueue Queue { get; } = queue ?? throw new ArgumentNullException(nameof(queue));
            public TileCoordinates StartLocation1 { get; } = startLocation1;
            public TileCoordinates StartLocation2 { get; } = startLocation2;
        }

        private enum BoxStatus
        {
            /// <summary>
            /// Not currently in use.
            /// In this state, the box can be reserved. 
            /// In all other states, the box is reserved and in use.
            /// </summary>
            None,

            /// <summary>
            /// Getting the players in the arena/box/coordinates.
            /// </summary>
            Starting,

            /// <summary>
            /// Match in progress.
            /// </summary>
            Playing,
        }

        private enum PlayerMatchmakingState
        {
            None,

            /// <summary>
            /// The player is being moved to the arena.
            /// </summary>
            SwitchingArena,

            /// <summary>
            /// The player is in the assigned arena, but has not yet been placed on a freq or ship.
            /// </summary>
            Waiting,

            /// <summary>
            /// The player is on the assigned freq and in a ship.
            /// </summary>
            Playing,

            /// <summary>
            /// The player was defeated.
            /// </summary>
            KnockedOut,

            /// <summary>
            /// The player left or changed to spectator mode, possibly due to lag.
            /// </summary>
            GaveUp,
        }

        private class BoxState
        {
            public BoxStatus Status = BoxStatus.None;

            public string? Player1Name;
            public Player? Player1;
            public PlayerMatchmakingState Player1State;

            public string? Player2Name;
            public Player? Player2;
            public PlayerMatchmakingState Player2State;

            public BoxState(int arenaNumber, int boxId)
            {
                MatchIdentifier = new(arenaNumber, boxId);
            }

            public MatchIdentifier MatchIdentifier { get; }

            public bool Reserve(Player player1, Player player2)
            {
                if (Status != BoxStatus.None)
                    return false;

                Status = BoxStatus.Starting;
                Player1 = player1 ?? throw new ArgumentNullException(nameof(player1));
                Player2 = player2 ?? throw new ArgumentNullException(nameof(player2));
                Player1Name = player1.Name;
                Player2Name = player2.Name;
                Player1State = Player2State = PlayerMatchmakingState.None;
                return true;
            }

            public void Reset()
            {
                Status = BoxStatus.None;

                Player1Name = Player2Name = null;
                Player1 = Player2 = null;
                Player1State = Player2State = PlayerMatchmakingState.None;
            }
        }

        private class ArenaData
        {
            public readonly BoxState[] Boxes;

            public ArenaData(int arenaNumber, int boxCount)
            {
                Boxes = new BoxState[boxCount];

                for (int boxId = 0; boxId < Boxes.Length; boxId++)
                {
                    Boxes[boxId] = new BoxState(arenaNumber, boxId);
                }
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// Identifies the match the player is in. <see langword="null"/> if not in a match.
            /// </summary>
            public MatchIdentifier? MatchIdentifier;

            /// <summary>
            /// The last ship the player used.
            /// This is used for spawning the player in the same ship in their next match.
            /// </summary>
            public ShipType? LastShip = null;

            /// <summary>
            /// Whether the player has entered the arena.
            /// Entered meaning the player has sent a position packet (definitely past map/lvz downloads).
            /// </summary>
            public bool HasEnteredArena = false;

            bool IResettable.TryReset()
            {
                MatchIdentifier = null;
                LastShip = null;
                HasEnteredArena = false;

                return true;
            }
        }

        private record MatchIdentifier(int ArenaNumber, int BoxId); // immutable, value equality
    }
}
