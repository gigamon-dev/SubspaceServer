using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using System.Collections.ObjectModel;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages 1v1 matchmaking.
    /// </summary>
    public class Match1v1 : IModule, IMatchmakingQueueAdvisor
    {
        private const string ConfigurationFileName = "Match1v1.conf";

        private IArenaManager _arenaManager;
        private IChat _chat;
        private IConfigManager _configManager;
        private IGame _game;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IMatchmakingQueues _playerQueues;

        private PlayerDataKey<PlayerData> _pdKey;
        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor> _iMatchmakingQueueAdvisorToken;

        private string _arenaBaseName;
        private int _maxArenas;
        private readonly List<string> _arenaNames = new(10); // for reducing string allocations
        private Dictionary<string, OneVersusOneQueue> _queueDictionary;
        private BoxConfiguration[] _boxes;
        private readonly Dictionary<int, ArenaData> _arenaDataDictionary = new(); // Not using per-arena data since it will have data for arenas that aren't created yet.

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IMatchmakingQueues playerQueues)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _playerQueues = playerQueues ?? throw new ArgumentNullException(nameof(playerQueues));

            if (!LoadConfiguration())
            {
                return false;
            }

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Register(broker, Callback_MatchmakingQueueChanged);

            _pdKey = _playerData.AllocatePlayerData(new PlayerDataPooledObjectPolicy());

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken);

            _playerData.FreePlayerData(ref _pdKey);

            foreach (var queue in _queueDictionary.Values)
            {
                _playerQueues.UnregisterQueue(queue);
            }
            _queueDictionary.Clear();

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            return true;
        }

        #endregion

        #region IMatchmakingQueueAdvisor

        string IMatchmakingQueueAdvisor.GetDefaultQueue(Arena arena)
        {
            if (string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase)
                && _boxes != null
                && _boxes.Length > 0)
            {
                return _boxes[0].Queue.Name;
            }

            return null;
        }

        string IMatchmakingQueueAdvisor.GetQueueNameByAlias(Arena arena, string alias)
        {
            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase)
                || _boxes == null
                || !int.TryParse(alias, out int boxNumber))
            {
                return null;
            }

            boxNumber -= 1; // zero-indexed

            if (boxNumber >= 0 && boxNumber < _boxes.Length)
            {
                return _boxes[boxNumber].Queue.Name;
            }

            return null;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
            {
                KillCallback.Register(arena, Callback_Kill);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            }
            else if (action == ArenaAction.Destroy)
            {
                KillCallback.Unregister(arena, Callback_Kill);
                ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterGame)
            {
                if (arena == null
                    || !string.Equals(arena.BaseName, _arenaBaseName)
                    || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                {
                    return;
                }

                playerData.HasEnteredArena = true;

                if (playerData.MatchIdentifier != null
                    && playerData.MatchIdentifier.ArenaNumber == arena.Number)
                {
                    if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData arenaData))
                        return;

                    BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];

                    if (boxState.Status == BoxStatus.Starting)
                    {
                        if (boxState.Player1 == player && boxState.Player1State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player1State = PlayerMatchmakingState.Waiting;
                            QueueMatchInitialzation(boxState.MatchIdentifier);
                        }
                        else if (boxState.Player2 == player && boxState.Player2State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player2State = PlayerMatchmakingState.Waiting;
                            QueueMatchInitialzation(boxState.MatchIdentifier);
                        }
                    }
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (arena == null || !string.Equals(arena.BaseName, _arenaBaseName))
                    return;

                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                playerData.HasEnteredArena = false;

                if (playerData.MatchIdentifier != null
                    && arena.Number == playerData.MatchIdentifier.ArenaNumber)
                {
                    if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData arenaData))
                        return;

                    BoxState boxState = arenaData.Boxes[playerData.MatchIdentifier.BoxId];
                    if (boxState.Status == BoxStatus.Playing)
                    {
                        if (boxState.Player1 == player)
                        {
                            boxState.Player1State = PlayerMatchmakingState.GaveUp;
                            QueueMatchCompletionCheck(boxState.MatchIdentifier);
                            _playerQueues.UnsetPlayingWithoutRequeue(player);
                        }
                        else if (boxState.Player2 == player)
                        {
                            boxState.Player2State = PlayerMatchmakingState.GaveUp;
                            QueueMatchCompletionCheck(boxState.MatchIdentifier);
                            _playerQueues.UnsetPlayingWithoutRequeue(player);
                        }
                    }
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                if (playerData.MatchIdentifier != null)
                {
                    if (!_arenaDataDictionary.TryGetValue(playerData.MatchIdentifier.ArenaNumber, out ArenaData arenaData))
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

        private void Callback_MatchmakingQueueChanged(IMatchmakingQueue queue, QueueAction action, QueueItemType itemType)
        {
            if (action != QueueAction.Add
                || !_queueDictionary.TryGetValue(queue.Name, out OneVersusOneQueue found)
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

            if (!killer.TryGetExtraData(_pdKey, out PlayerData killerPlayerData) || killerPlayerData.MatchIdentifier == null)
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedPlayerData) || killedPlayerData.MatchIdentifier == null || killerPlayerData.MatchIdentifier != killedPlayerData.MatchIdentifier)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData arenaData))
                return;

            BoxState boxState = arenaData.Boxes[killedPlayerData.MatchIdentifier.BoxId];
            if (boxState.Status == BoxStatus.Playing)
            {
                if (boxState.Player1 == killer && boxState.Player2 == killed)
                {
                    boxState.Player2State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier);
                }
                else if (boxState.Player2 == killer && boxState.Player1 == killed)
                {
                    boxState.Player1State = PlayerMatchmakingState.KnockedOut;
                    QueueMatchCompletionCheck(boxState.MatchIdentifier);
                }
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena arena = player.Arena;
            if (!string.Equals(arena.BaseName, _arenaBaseName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData arenaData))
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData) || playerData.MatchIdentifier == null)
                return;

            int boxId = playerData.MatchIdentifier.BoxId;
            BoxState boxState = arenaData.Boxes[boxId];
            if (boxState.Status == BoxStatus.Playing)
            {
                BoxConfiguration boxConfig = _boxes[boxId];

                if (newShip == ShipType.Spec)
                {
                    if (boxState.Player1 == player && boxState.Player1State == PlayerMatchmakingState.Playing)
                    {
                        boxState.Player1State = PlayerMatchmakingState.GaveUp;
                        QueueMatchCompletionCheck(boxState.MatchIdentifier);
                        _playerQueues.UnsetPlayingWithoutRequeue(player);
                    }
                    else if (boxState.Player2 == player && boxState.Player2State == PlayerMatchmakingState.Playing)
                    {
                        boxState.Player2State = PlayerMatchmakingState.GaveUp;
                        QueueMatchCompletionCheck(boxState.MatchIdentifier);
                        _playerQueues.UnsetPlayingWithoutRequeue(player);
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

        #endregion

        private bool LoadConfiguration()
        {
            ConfigHandle ch = _configManager.OpenConfigFile(null, ConfigurationFileName);
            if (ch == null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Match1v1), $"Error opening {ConfigurationFileName}.");
                return false;
            }

            try
            {
                _arenaBaseName = _configManager.GetStr(ch, "Matchmaking", "ArenaBaseName");
                _maxArenas = _configManager.GetInt(ch, "Matchmaking", "MaxArenas", 10);
                if (_maxArenas < 1)
                    _maxArenas = 1;

                int boxCount = _configManager.GetInt(ch, "Matchmaking", "Boxes", 0);
                _boxes = new BoxConfiguration[boxCount];
                _queueDictionary = new(boxCount);

                for (int i = 0; i < _boxes.Length; i++)
                {
                    int box = i + 1;

                    string queueName = _configManager.GetStr(ch, $"Box{box}", "Queue");
                    if (string.IsNullOrWhiteSpace(queueName))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Match1v1), $"Invalid Queue for Box{box}.");
                        return false;
                    }

                    string coordinateStr = _configManager.GetStr(ch, $"Box{box}", "StartPlayer1");
                    if (!MapCoordinate.TryParse(coordinateStr, out MapCoordinate startLocation1))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Match1v1), $"Invalid StartPlayer1 for Box{box}.");
                        return false;
                    }

                    coordinateStr = _configManager.GetStr(ch, $"Box{box}", "StartPlayer2");
                    if (!MapCoordinate.TryParse(coordinateStr, out MapCoordinate startLocation2))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Match1v1), $"Invalid StartPlayer2 for Box{box}.");
                        return false;
                    }

                    if (!_queueDictionary.TryGetValue(queueName, out OneVersusOneQueue queue))
                    {
                        string description = _configManager.GetStr(ch, $"Queue-{queueName}", "Description");
                        bool allowAutoRequeue = _configManager.GetInt(ch, $"Queue-{queueName}", "AllowAutoRequeue", 0) != 0;

                        queue = new OneVersusOneQueue(
                            queueName,
                            new QueueOptions
                            {
                                AllowSolo = true,
                                AllowGroups = false,
                                AllowAutoRequeue = true,
                            },
                            description);

                        if (!_playerQueues.RegisterQueue(queue))
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Match1v1), $"Failed to register queue '{queueName}' (used by Box{box}).");
                            return false;
                        }

                        _queueDictionary.Add(queueName, queue);
                    }

                    queue.AddBox(i);
                    _boxes[i] = new BoxConfiguration(queue, startLocation1, startLocation2);
                }
            }
            finally
            {
                _configManager.CloseConfigFile(ch);
            }

            return true;
        }

        private bool DoMatching(OneVersusOneQueue queue)
        {
            if (queue == null)
                return false;

            // Find an available spot for the match..
            if (!TryGetAvailableArenaAndBox(queue, out int arenaNumber, out int boxId, out ArenaData arenaData))
            {
                return false;
            }

            // Check if there are 2 players available.
            if (!queue.GetNext(out Player player1, out Player player2))
                return false;

            if (!player1.TryGetExtraData(_pdKey, out PlayerData player1Data)
                || !player2.TryGetExtraData(_pdKey, out PlayerData player2Data))
            {
                queue.UndoNext(player1, player2);
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

                _playerQueues.SetPlaying(set, null);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }

            // Get the players into the correct arena if they aren't already there.
            // TODO: If the player is not in spec, then notify them of the match and which arena to go to and give them 30 seconds to get there.
            string arenaName = GetArenaName(arenaNumber);
            Arena arena = _arenaManager.FindArena(arenaName); // This will only find the arena if it already exists and is running.

            if (arena != null && player1.Arena == arena)
            {
                boxState.Player1State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player1State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player1, arenaName, 0, 0);
            }

            if (arena != null && player2.Arena == arena)
            {
                boxState.Player2State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player2State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player2, arenaName, 0, 0);
            }

            QueueMatchInitialzation(boxState.MatchIdentifier);
            return true;

            bool TryGetAvailableArenaAndBox(OneVersusOneQueue queue, out int arenaNumber, out int boxId, out ArenaData arenaData)
            {
                arenaNumber = 0;

                do
                {
                    // arena data
                    if (!_arenaDataDictionary.TryGetValue(arenaNumber, out arenaData))
                    {
                        arenaData = new ArenaData(arenaNumber, _boxes.Length);
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

                // no availablity
                arenaNumber = 0;
                arenaData = null;
                boxId = 0;
                return false;
            }

            static bool TryGetAvailableBox(ArenaData arenaData, OneVersusOneQueue queue, out int boxId)
            {
                foreach (int id in queue.BoxIds)
                {
                    if (arenaData.Boxes[id].Status == BoxStatus.None)
                    {
                        boxId = id;
                        return true;
                    }
                }

                boxId = default;
                return false;
            }
        }

        private string GetArenaName(int arenaNumber)
        {
            if (arenaNumber < _arenaNames.Count)
            {
                return _arenaNames[arenaNumber];
            }
            else
            {
                string arenaName = Arena.CreateArenaName(_arenaBaseName, arenaNumber);
                _arenaNames.Add(arenaName);
                return arenaName;
            }
        }

        private void QueueMatchInitialzation(MatchIdentifier matchIdentifier)
        {
            _mainloop.QueueMainWorkItem(DoMatchInitialization, matchIdentifier);

            void DoMatchInitialization(MatchIdentifier matchWorkItem)
            {
                string arenaName = GetArenaName(matchIdentifier.ArenaNumber);
                Arena arena = _arenaManager.FindArena(arenaName);
                if (arena == null)
                    return;

                if (!_arenaDataDictionary.TryGetValue(matchWorkItem.ArenaNumber, out ArenaData arenaData))
                    return;

                int boxId = matchWorkItem.BoxId;
                BoxState boxState = arenaData.Boxes[boxId];

                if (boxState.Status == BoxStatus.Starting)
                {
                    Player player1 = boxState.Player1;
                    if (!player1.TryGetExtraData(_pdKey, out PlayerData player1Data))
                        return;

                    Player player2 = boxState.Player2;
                    if (!player2.TryGetExtraData(_pdKey, out PlayerData player2Data))
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
                        _game.WarpTo(player1, _boxes[boxId].StartLocation1.X, _boxes[boxId].StartLocation1.Y);
                        _game.WarpTo(player2, _boxes[boxId].StartLocation2.X, _boxes[boxId].StartLocation2.Y);

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
                    if (playerData.LastShip != null)
                        ship = playerData.LastShip.Value;
                    else
                        playerData.LastShip = ship = ShipType.Warbird;

                    _game.SetShipAndFreq(player, ship, freq);
                }
            }
        }

        private void QueueMatchCompletionCheck(MatchIdentifier matchIdentifier)
        {
            _mainloopTimer.SetTimer(CheckMatchCompletion, 2000, Timeout.Infinite, matchIdentifier, matchIdentifier);

            bool CheckMatchCompletion(MatchIdentifier matchIdentifier)
            {
                Arena arena = _arenaManager.FindArena(_arenaNames[matchIdentifier.ArenaNumber]);
                if (arena == null)
                    return false;

                if (!_arenaDataDictionary.TryGetValue(matchIdentifier.ArenaNumber, out ArenaData arenaData))
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
                        if (boxState.Player1 != null)
                            _chat.SendMessage(boxState.Player1, "You left the match.");

                        if (boxState.Player2 != null)
                            _chat.SendMessage(boxState.Player2, "Your opponent left the match.");
                    }

                    if (boxState.Player2State == PlayerMatchmakingState.GaveUp)
                    {
                        if (boxState.Player2 != null)
                            _chat.SendMessage(boxState.Player2, "You left the match.");

                        if (boxState.Player1 != null)
                            _chat.SendMessage(boxState.Player1, "Your opponent left the match.");
                    }

                    EndMatch(arena, matchIdentifier.BoxId, boxState, OneVersusOneMatchEndReason.Aborted, null);
                }

                return false;

                void EndMatch(Arena arena, int boxId, BoxState boxState, OneVersusOneMatchEndReason reason, int? winner)
                {
                    string winnerPlayerName = winner switch
                    {
                        1 => boxState.Player1Name,
                        2 => boxState.Player2Name,
                        _ => null
                    };

                    OneVersusOneMatchEndedCallback.Fire(arena, arena, boxId, reason, winnerPlayerName);

                    List<PlayerOrGroup> players = _playerQueues.PlayerOrGroupListPool.Get();
                    bool queuedMainloopWork = false;

                    try
                    {
                        if (boxState.Player1 != null)
                            players.Add(new PlayerOrGroup(boxState.Player1));

                        if (boxState.Player2 != null)
                            players.Add(new PlayerOrGroup(boxState.Player2));

                        // Clear match info.
                        boxState.Reset();

                        if (players.Count > 0)
                        {
                            foreach (PlayerOrGroup pog in players)
                            {
                                Player player = pog.Player;
                                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                                    continue;

                                playerData.MatchIdentifier = null;

                                // Change to spectator mode.
                                // NOTE: The ShipFreqChangeCallback gets called asynchronously as a mainloop workitem.
                                if (player.Ship != ShipType.Spec)
                                    _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                            }

                            // Remove the players' 'Playing' state with allowed requeuing.
                            // Since this can requeue, it will fire the MatchmakingQueueChangedCallback for any that are requeued.
                            // However, this must definitely happen after the ShipFreqChangeCallback(s).
                            // So, queue this as a mainloop workitem too, which will happen after the ShipFreqChangeCallback(s) occur.
                            _mainloop.QueueMainWorkItem(DoUnsetPlaying, players);
                            queuedMainloopWork = true;
                        }
                    }
                    finally
                    {
                        if (!queuedMainloopWork)
                            _playerQueues.PlayerOrGroupListPool.Return(players);
                    }

                    void DoUnsetPlaying(List<PlayerOrGroup> players)
                    {
                        try
                        {
                            _playerQueues.UnsetPlaying(players);
                        }
                        finally
                        {
                            _playerQueues.PlayerOrGroupListPool.Return(players);
                        }
                    }
                }
            }
        }

        private class OneVersusOneQueue : IMatchmakingQueue
        {
            // The idea is to work like a ski lift queue with 1 group line and 1 solo line.
            // Try to take from the group line, and then fill in remaining slots using the solo line.
            public readonly LinkedList<Player> SoloQueue; // TODO: pooling of LinkListNode<Player> objects
            //public readonly LinkedList<IPlayerGroup> GroupQueue; // insert in order of group size

            private readonly List<int> _boxIds = new();

            public OneVersusOneQueue(
                string queueName,
                QueueOptions options,
                string description)
            {
                if (string.IsNullOrWhiteSpace(queueName))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(queueName));

                if (!options.AllowSolo && !options.AllowGroups)
                    throw new ArgumentException($"At minimum {nameof(options.AllowSolo)} or {nameof(options.AllowGroups)} must be true.", nameof(options));

                Name = queueName;
                Options = options;
                Description = description;

                BoxIds = _boxIds.AsReadOnly();

                if (options.AllowSolo)
                    SoloQueue = new LinkedList<Player>();

                if (options.AllowGroups)
                    throw new ArgumentException("1v1 can't allow groups.", nameof(options));
            }

            public string Name { get; }
            public QueueOptions Options { get; }
            public string Description { get; }

            public void AddBox(int boxId)
            {
                _boxIds.Add(boxId);
            }

            public ReadOnlyCollection<int> BoxIds { get; }

            public bool Add(Player player)
            {
                if (SoloQueue == null)
                    return false;

                SoloQueue.AddLast(player);
                return true;
            }

            public bool Add(IPlayerGroup group)
            {
                //if (GroupQueue == null)
                return false;

                // TODO: find the correct spot in the queue (ordered by # of players? or by time added only
                //return true;
            }

            public bool Remove(Player player)
            {
                return SoloQueue.Remove(player);
            }

            public bool Remove(IPlayerGroup group)
            {
                return false; // TODO:
            }

            public void GetQueued(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups)
            {
                if (soloPlayers != null)
                {
                    soloPlayers.UnionWith(SoloQueue);
                }

                if (groups != null)
                {
                    // groups purposely not supported in this queue
                }
            }

            public bool GetNext(out Player player1, out Player player2)
            {
                if (SoloQueue.Count < 2)
                {
                    player1 = null;
                    player2 = null;
                    return false;
                }

                // player 1
                var node = SoloQueue.First;
                player1 = node.Value;
                SoloQueue.Remove(node);

                // player 2
                node = SoloQueue.First;
                player2 = node.Value;
                SoloQueue.Remove(node);

                return true;
            }

            public void UndoNext(Player player1, Player player2)
            {
                if (player2 != null)
                    SoloQueue.AddFirst(player2);

                if (player1 != null)
                    SoloQueue.AddFirst(player1);
            }

            //public bool GetNext(int count, HashSet<Player> soloPlayers, HashSet<IPlayerGroup> playerGroups)
            //{
            //    // TODO: 
            //    return false;
            //}

            //bool GetMatch(string queueName, List<HashSet<Player>> teams);
            //bool GetNext(string queueName, out Player player);
            //bool GetNext(string queueName, int count, HashSet<Player> players);
            //bool SetMatchComplete(HashSet<Player> players);
        }

        private struct BoxConfiguration
        {
            public BoxConfiguration(OneVersusOneQueue queue, MapCoordinate startLocation1, MapCoordinate startLocation2)
            {
                Queue = queue ?? throw new ArgumentNullException(nameof(queue));
                StartLocation1 = startLocation1;
                StartLocation2 = startLocation2;
            }

            public OneVersusOneQueue Queue { get; }
            public MapCoordinate StartLocation1 { get; }
            public MapCoordinate StartLocation2 { get; }
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

            public string Player1Name;
            public Player Player1;
            public PlayerMatchmakingState Player1State;

            public string Player2Name;
            public Player Player2;
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

        private class PlayerData
        {
            /// <summary>
            /// Identifies the match the player is in. <see langword="null"/> if not in a match.
            /// </summary>
            public MatchIdentifier MatchIdentifier;

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
        }

        private class PlayerDataPooledObjectPolicy : IPooledObjectPolicy<PlayerData>
        {
            public PlayerData Create()
            {
                return new PlayerData();
            }

            public bool Return(PlayerData obj)
            {
                if (obj == null)
                    return false;

                obj.MatchIdentifier = null;
                obj.LastShip = null;
                obj.HasEnteredArena = false;

                return true;
            }
        }

        private record MatchIdentifier(int ArenaNumber, int BoxId); // immutable, value equality
    }
}
