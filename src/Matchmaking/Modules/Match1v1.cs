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
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IMatchmakingQueues _playerQueues;

        //private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;
        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor> _iMatchmakingQueueAdvisorToken;

        private string _arenaBaseName;
        private List<string> _arenaNames = new(10); // for reducing string allocations
        private Dictionary<string, OneVersusOneQueue> _queueDictionary;
        private BoxConfiguration[] _boxes;
        private Dictionary<int, ArenaData> _arenaDataDictionary = new(); // Note: can't use per-arena data since this needs contain data for arenas that aren't yet created

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IMatchmakingQueues playerQueues)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
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

            //_adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken);

            //_arenaManager.FreeArenaData(_adKey);
            _playerData.FreePlayerData(_pdKey);

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
            
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.LeaveArena)
            {
                if (arena == null || !string.Equals(arena.BaseName, _arenaBaseName))
                    return;

                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                playerData.HasEnteredArena = false;
            }

            if (action == PlayerAction.EnterGame)
            {
                if (arena == null
                    || !string.Equals(arena.BaseName, _arenaBaseName)
                    || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                {
                    return;
                }

                playerData.HasEnteredArena = true;

                if (playerData.MatchArenaNumber == arena.Number
                    && playerData.MatchBox != null)
                {
                    if (!_arenaDataDictionary.TryGetValue(arena.Number, out ArenaData arenaData))
                        return;

                    BoxState boxState = arenaData.Boxes[playerData.MatchBox.Value];

                    if (boxState.Status == BoxStatus.Starting)
                    {
                        if (boxState.Player1 == player && boxState.Player1State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player1State = PlayerMatchmakingState.Waiting;
                        }

                        if (boxState.Player2 == player && boxState.Player2State == PlayerMatchmakingState.SwitchingArena)
                        {
                            boxState.Player2State = PlayerMatchmakingState.Waiting;
                        }

                        DoMatchInitialization(arenaData, playerData.MatchBox.Value);
                    }
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

            DoMatching(found);
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

        private void DoMatching(OneVersusOneQueue queue)
        {
            if (queue == null)
                return;

            // check if there are 2 players available
            if (!queue.GetNext(out Player player1, out Player player2))
                return;

            if (!player1.TryGetExtraData(_pdKey, out PlayerData player1Data))
                return;

            if (!player2.TryGetExtraData(_pdKey, out PlayerData player2Data))
                return;

            // find an available arena and box
            string arenaName;
            ArenaData arenaData;
            int boxId;

            int arenaNumber = 0;
            do
            {
                if (arenaNumber < _arenaNames.Count)
                {
                    arenaName = _arenaNames[arenaNumber];
                }
                else
                {
                    arenaName = Arena.CreateArenaName(_arenaBaseName, arenaNumber);
                    _arenaNames.Add(arenaName);
                }

                if (!_arenaDataDictionary.TryGetValue(arenaNumber, out arenaData))
                {
                    arenaData = new ArenaData(_boxes.Length);
                    _arenaDataDictionary.Add(arenaNumber, arenaData);
                }

                if (TryGetAvailableBox(arenaData, queue, out boxId))
                {
                    break;
                }

                arenaNumber++;
            }
            while (true);

            // got an arena and box
            player1Data.MatchArenaNumber = player2Data.MatchArenaNumber = arenaNumber;
            player1Data.MatchBox = player2Data.MatchBox = boxId;

            BoxState boxState = arenaData.Boxes[boxId];
            boxState.Status = BoxStatus.Starting;
            boxState.Player1 = player1;
            boxState.Player2 = player2;

            // get the players in the correct arena
            if (string.Equals(player1.Arena.Name, arenaName, StringComparison.OrdinalIgnoreCase))
            {
                boxState.Player1State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player1State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player1, arenaName, 0, 0);
            }

            if (string.Equals(player2.Arena.Name, arenaName, StringComparison.OrdinalIgnoreCase))
            {
                boxState.Player2State = PlayerMatchmakingState.Waiting;
            }
            else
            {
                boxState.Player2State = PlayerMatchmakingState.SwitchingArena;
                _arenaManager.SendToArena(player2, arenaName, 0, 0);
            }

            DoMatchInitialization(arenaData, boxId);

            bool TryGetAvailableBox(ArenaData arenaData, OneVersusOneQueue queue, out int boxId)
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

        private void DoMatchInitialization(ArenaData arenaData, int boxId)
        {
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
                    // The players are in the correct arena.
                    // set freq/ship and warp to the proper box starting location

                    // TOOD: freqs
                    short freq1 = 0;
                    short freq2 = 1;

                    SetShipAndFreq(player1, player1Data, freq1);
                    SetShipAndFreq(player2, player2Data, freq2);
                    _game.WarpTo(player1, _boxes[boxId].StartLocation1.X, _boxes[boxId].StartLocation1.Y);
                    _game.WarpTo(player2, _boxes[boxId].StartLocation2.X, _boxes[boxId].StartLocation2.Y);

                    boxState.Status = BoxStatus.Playing;
                    boxState.Player1State = boxState.Player2State = PlayerMatchmakingState.Playing;
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

        private class OneVersusOneQueue : IMatchmakingQueue
        {
            // The idea is to work like a ski lift queue with 1 group line and 1 solo line.
            // Try to take from the group line, and then fill in remaining slots using the solo line.
            public readonly LinkedList<Player> SoloQueue; // TODO: pooling of LinkListNode<Player> objects
            //public readonly LinkedList<IPlayerGroup> GroupQueue; // insert in order of group size

            private List<int> _boxIds = new();

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

            /// <summary>
            /// Short delay after a game ends.
            /// Transitions to <see cref="None"/>.
            /// </summary>
            Ending,
        }

        private enum PlayerMatchmakingState
        {
            None,

            /// <summary>
            /// The player is being moved to the arena.
            /// </summary>
            SwitchingArena,

            /// <summary>
            /// The player is in the assigned arena, waiting for the opponent to enter.
            /// </summary>
            Waiting,

            /// <summary>
            /// The player is on the assigned freq and in a ship.
            /// </summary>
            Playing,
        }

        private class BoxState
        {
            public BoxStatus Status = BoxStatus.None;

            public Player Player1;
            public PlayerMatchmakingState Player1State;

            public Player Player2;
            public PlayerMatchmakingState Player2State;
        }

        private class ArenaData
        {
            public readonly BoxState[] Boxes;

            public ArenaData(int boxCount)
            {
                Boxes = new BoxState[boxCount];

                for (int i = 0; i < Boxes.Length; i++)
                {
                    Boxes[i] = new BoxState();
                }
            }
        }

        private class PlayerData
        {
            public int? MatchArenaNumber = null;
            public int? MatchBox = null;

            public ShipType? LastShip = null;

            /// <summary>
            /// Whether the player has entered the arena.
            /// Entered meaning the player has sent a position packet (definitely past map/lvz downloads).
            /// </summary>
            public bool HasEnteredArena = false;
        }
    }
}
