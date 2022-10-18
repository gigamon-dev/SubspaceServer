using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Packets.Game;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module for NvN matches.
    /// </summary>
    /// <remarks>
    /// Win conditions:
    /// - all players of a team knocked out
    /// - at least one team member has to be in the play area (respawn area separated), like in 2v2
    /// - match time limit hit and one team has a higher # of lives remaining
    /// - in overtime and a kill is made
    /// 
    /// Subbing (IDEA)
    /// -------
    /// If a player leaves or goes to spec during a game, they have 30 seconds to get back in, after which their spot it forfeit and can be subbed.
    /// Players that leave their game in this manner can be penalized (disallow queuing for a certain timeframe, 10 minutes?).
    /// Alternatively, a player can request to be subbed (no penalization).
    /// A player that subs in does not lose their spot in the queue. They can effectively be at the front of the queue when the game ends and get to play in the next game.
    /// 
    /// Time limit
    /// ----------
    /// 30 minutes, 
    /// then 5 minutes of overtime (next kill wins), 
    /// then maybe sudden death - burn items, lower max energy of each player by 1 unit every minute until at the minimum max energy, then lower max recharge rate of each player by 1 every minute
    /// </remarks>
    public class TeamVersusMatch : IModule, IMatchmakingQueueAdvisor
    {
        private const string ConfigurationFileName = $"{nameof(TeamVersusMatch)}.conf";

        private IArenaManager _arenaManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private IGame _game;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IMatchmakingQueues _matchmakingQueues;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;

        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor> _iMatchmakingQueueAdvisorToken;

        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// Dictionary of queues.
        /// </summary>
        /// <remarks>
        /// key: queue name
        /// </remarks>
        private readonly Dictionary<string, TeamVersusMatchmakingQueue> _queueDictionary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Dictionary of match configurations.
        /// </summary>
        /// <remarks>
        /// key: match type
        /// </remarks>
        private readonly Dictionary<string, MatchConfiguration> _matchConfigurationDictionary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Dictionary of matches.
        /// </summary>
        private readonly Dictionary<MatchIdentifier, MatchData> _matchDataDictionary = new();

        /// <summary>
        /// Dictionary for looking up what slot in a match a player is assigned.
        /// <para>
        /// A player is in a match until subbed out or the match ends. 
        /// A player that leaves is still considered to be in the match and can ?return to it if they have not yet been subbed.
        /// </para>
        /// </summary>
        /// <remarks>
        /// key: player name
        /// </remarks>
        private readonly Dictionary<string, PlayerSlot> _playerSlotDictionary = new(StringComparer.OrdinalIgnoreCase);

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IMatchmakingQueues matchmakingQueues,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _matchmakingQueues = matchmakingQueues ?? throw new ArgumentNullException(nameof(matchmakingQueues));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            if (!LoadConfiguration())
            {
                return false;
            }

            _pdKey = _playerData.AllocatePlayerData(new PlayerDataPooledObjectPolicy());

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Register(broker, Callback_MatchmakingQueueChanged);

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
            {
                KillCallback.Register(arena, Callback_Kill);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);

                //commandManager.AddCommand("requestsub", Command_requestsub, arena); // allow a player request to be subbed out
                //commandManager.AddCommand("sub", Command_sub, arena); // for a player to confirm that they agree to sub in
                _commandManager.AddCommand("return", Command_return, arena); // return to the game after being spec'd or disconnected
                _commandManager.AddCommand("restart", Command_restart, arena); // request that the game be restarted, needs a majority vote
                _commandManager.AddCommand("randomize", Command_randomize, arena); // request that teams be re-randomized, needs a majority vote --> using would ignore player groups
                _commandManager.AddCommand("mercy", Command_mercy, arena); // if all remaining players on a team agree (type the command), the game will end as a loss to that team without having to be killed out.
                //_commandManager.AddCommand("end", Command_mercy, arena); // request that the game be ended (no with no winner), needs a majority vote
            }
            else if (action == ArenaAction.Destroy)
            {
                KillCallback.Unregister(arena, Callback_Kill);
                ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);

                //commandManager.RemoveCommand("requestsub", Command_requestsub, arena);
                //commandManager.RemoveCommand("sub", Command_sub, arena);
                _commandManager.RemoveCommand("return", Command_return, arena);
                _commandManager.RemoveCommand("restart", Command_restart, arena);
                _commandManager.RemoveCommand("randomize", Command_randomize, arena);
                _commandManager.RemoveCommand("mercy", Command_mercy, arena);
                //_commandManager.RemoveCommand("end", Command_mercy, arena);
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {

        }

        private void Callback_MatchmakingQueueChanged(IMatchmakingQueue queue, QueueAction action, QueueItemType itemType)
        {
            if (action != QueueAction.Add
                || !_queueDictionary.TryGetValue(queue.Name, out TeamVersusMatchmakingQueue found)
                || found != queue)
            {
                return;
            }

            // Match until no more matches can be made.
            while (MakeMatch(found)) { }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {

        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {

        }

        #endregion

        #region Commands

        private void Command_return(string commandName, string parameters, Player player, ITarget target)
        {

        }

        private void Command_restart(string commandName, string parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requests to restart the game. To agree, type: ?restart");
            //_chat.SendMessage(, $"{player.Name} agreed to ?restart. ({forVotes}/5)");
            //_chat.SendMessage(, $"The vote to ?restart has expired.");
        }

        private void Command_randomize(string commandName, string parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requests to re-randomize the teams and restart the game. To agree, type: ?randomize");
            //_chat.SendMessage(, $"{player.Name} agreed to ?randomize. ({forVotes}/5)");
            //_chat.SendMessage(, $"The vote to ?randomize has expired.");
        }

        private void Command_mercy(string commandName, string parameters, Player player, ITarget target)
        {
            // To the opposing team (voting begin)
            //_chat.SendMessage(, $"{player.Name} has requested their team to surrender. Their team is now voting on it.");

            // To the opposing team (vote passed or no vote needed - last player)
            //_chat.SendMessage(, $"Your opponents have surrendered with honor.");

            // To the opposing team (vote failed)
            //_chat.SendMessage(, $"Your opponents chose to not surrender. Show no mercy!");


            // To the player's team (voting begin)
            //_chat.SendMessage(, $"Your teammate, {player.Name}, has requested to surrender with honor. To agree, type: ?mercy");

            // To the player's team (vote passed or no vote needed - last player)
            //_chat.SendMessage(, $"Your team has surrendered with honor.");

            // To the player's team (vote expired)
            //_chat.SendMessage(, $"The vote to surrender has expired. Fight on!");
        }

        #endregion

        #region IMatchmakingQueueAdvisor

        string IMatchmakingQueueAdvisor.GetDefaultQueue(Arena arena)
        {
            //if (string.Equals(arena.BaseName, BaseArenaName, StringComparison.OrdinalIgnoreCase))
            //return QueueName;

            return null;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerQueueChanged(string queueName, QueueAction action)
        {
            //if (string.Equals(queueName, QueueName, StringComparison.OrdinalIgnoreCase) || action != QueueAction.Add)
            //return;

            // TODO: if there's an ongoing game that needs a player, try to get one for it

            // TODO: check if there are enough players available to begin a new game
            // for a new game, find the next available arena, create one if needed, move players to the arena, ship and freq change, warp to, and begin countdown
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
                int i = 1;
                string matchType;
                while (!string.IsNullOrWhiteSpace(matchType = _configManager.GetStr(ch, "Matchmaking", $"Match{i++}")))
                {
                    if (_matchConfigurationDictionary.TryGetValue(matchType, out MatchConfiguration matchConfiguration))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Match {matchType} already exists. Check configuration for a duplicate.");
                        continue;
                    }

                    matchConfiguration = LoadMatchConfiguration(ch, matchType);
                    if (matchConfiguration == null)
                    {
                        continue;
                    }

                    string queueName = matchConfiguration.QueueName;
                    if (!_queueDictionary.TryGetValue(queueName, out TeamVersusMatchmakingQueue queue))
                    {
                        string queueSection = $"Queue-{queueName}";

                        string description = _configManager.GetStr(ch, queueSection, "Description");

                        bool allowSolo = _configManager.GetInt(ch, queueSection, "AllowSolo", 0) != 0;
                        bool allowGroups = _configManager.GetInt(ch, queueSection, "AllowGroups", 0) != 0;

                        if (!allowSolo && !allowGroups)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Invalid configuration for queue:{queueName}. It doesn't allow solo players or groups. Skipping.");
                            continue;
                        }

                        int? minGroupSize = null;
                        int? maxGroupSize = null;

                        if (allowGroups)
                        {
                            int val = _configManager.GetInt(ch, queueSection, "MinGroupSize", -1);
                            if (val != -1)
                            {
                                minGroupSize = val;
                            }

                            val = _configManager.GetInt(ch, queueSection, "MaxGroupSize", -1);
                            if (val != -1)
                            {
                                maxGroupSize = val;
                            }
                        }

                        // TODO: for now only allowing groups of the exact size needed (for simplified matching)
                        if (minGroupSize != matchConfiguration.PlayersPerTeam
                            || maxGroupSize != matchConfiguration.PlayersPerTeam)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Unsupported configuration for match '{matchConfiguration.MatchType}'. Queue '{queueName}' can't be used (must only allow groups of exactly {matchConfiguration.PlayersPerTeam} players).");
                            continue;
                        }

                        bool allowAutoRequeue = _configManager.GetInt(ch, queueSection, "AllowAutoRequeue", 0) != 0;

                        QueueOptions options = new()
                        {
                            AllowSolo = allowSolo,
                            AllowGroups = allowGroups,
                            MinGroupSize = minGroupSize,
                            MaxGroupSize = maxGroupSize,
                            AllowAutoRequeue = allowAutoRequeue,
                        };

                        queue = new(queueName, options, description);

                        if (!_matchmakingQueues.RegisterQueue(queue))
                        {
                            _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Failed to register queue '{queueName}' (used by match:{matchConfiguration.MatchType}).");
                            return false;
                        }

                        _queueDictionary.Add(queueName, queue);
                    }

                    queue.AddMatchConfiguration(matchConfiguration);
                    _matchConfigurationDictionary.Add(matchType, matchConfiguration);
                }
            }
            finally
            {
                _configManager.CloseConfigFile(ch);
            }

            return true;

            MatchConfiguration LoadMatchConfiguration(ConfigHandle ch, string matchType)
            {
                string queueName = _configManager.GetStr(ch, matchType, "Queue");
                if (string.IsNullOrWhiteSpace(queueName))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid Queue for Match '{matchType}'.");
                    return null;
                }

                string arenaBaseName = _configManager.GetStr(ch, matchType, "ArenaBaseName");
                if (string.IsNullOrWhiteSpace(arenaBaseName))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid ArenaBaseName for Match '{matchType}'.");
                    return null;
                }

                int maxArenas = _configManager.GetInt(ch, matchType, "MaxArenas", 0);
                if (maxArenas <= 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid MaxArenas for Match '{matchType}'.");
                    return null;
                }

                int numBoxes = _configManager.GetInt(ch, matchType, "NumBoxes", 0);
                if (numBoxes <= 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid NumBoxes for Match '{matchType}'.");
                    return null;
                }

                int numTeams = _configManager.GetInt(ch, matchType, "NumTeams", 0);
                if (numTeams <= 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid NumTeams for Match '{matchType}'.");
                    return null;
                }

                int playersPerTeam = _configManager.GetInt(ch, matchType, "PlayerPerTeam", 0);
                if (playersPerTeam <= 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid PlayerPerTeam for Match '{matchType}'.");
                    return null;
                }

                int livesPerPlayer = _configManager.GetInt(ch, matchType, "LivesPerPlayer", 0);
                if (livesPerPlayer <= 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid LivesPerPlayer for Match '{matchType}'.");
                    return null;
                }

                TimeSpan? timeLimit = null;
                string timeLimitStr = _configManager.GetStr(ch, matchType, "TimeLimit");
                if (!string.IsNullOrWhiteSpace(timeLimitStr))
                {
                    if (!TimeSpan.TryParse(timeLimitStr, out TimeSpan limit))
                    {
                        _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid TimeLimit for Match '{matchType}'.");
                        return null;
                    }
                    else
                    {
                        timeLimit = limit;
                    }
                }

                TimeSpan? overTimeLimit = null;
                if (timeLimit != null)
                {
                    string overTimeLimitStr = _configManager.GetStr(ch, matchType, "OverTimeLimit");
                    if (!string.IsNullOrWhiteSpace(overTimeLimitStr))
                    {
                        if (!TimeSpan.TryParse(overTimeLimitStr, out TimeSpan limit))
                        {
                            _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid OverTimeLimit for Match '{matchType}'.");
                            return null;
                        }
                        else
                        {
                            overTimeLimit = limit;
                        }
                    }
                }

                string winConditionDelayStr = _configManager.GetStr(ch, matchType, "WinConditionDelay");
                if (string.IsNullOrWhiteSpace(winConditionDelayStr)
                    || !TimeSpan.TryParse(winConditionDelayStr, out TimeSpan winConditionDelay))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid WinConditionDelay for Match '{matchType}'.");
                    return null;
                }

                MatchConfiguration matchConfiguration = new()
                {
                    MatchType = matchType,
                    QueueName = queueName,
                    ArenaBaseName = arenaBaseName,
                    MaxArenas = maxArenas,
                    NumTeams = numTeams,
                    PlayersPerTeam = playersPerTeam,
                    LivesPerPlayer = livesPerPlayer,
                    TimeLimit = timeLimit,
                    OverTimeLimit = overTimeLimit,
                    WinConditionDelay = winConditionDelay,
                    Boxes = new MatchBoxConfiguration[numBoxes],
                };

                if (!LoadMatchBoxesConfiguration(ch, matchType, matchConfiguration))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid configuration of boxes for Match '{matchType}'.");
                    return null;
                }

                return matchConfiguration;
            }

            bool LoadMatchBoxesConfiguration(ConfigHandle ch, string matchIdentifier, MatchConfiguration matchConfiguration)
            {
                List<MapCoordinate> tempCoordList = new();

                for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                {
                    int boxNumber = boxIdx + 1; // configuration is 1 based
                    string boxSection = $"{matchIdentifier}-Box{boxNumber}";

                    string playAreaMapRegion = _configManager.GetStr(ch, boxSection, "PlayAreaMapRegion");

                    MatchBoxConfiguration boxConfiguration = new()
                    {
                        TeamStartLocations = new MapCoordinate[matchConfiguration.NumTeams][],
                        PlayAreaMapRegion = playAreaMapRegion,
                    };

                    for (int teamIdx = 0; teamIdx < matchConfiguration.NumTeams; teamIdx++)
                    {
                        int teamNumber = teamIdx + 1;

                        if (tempCoordList.Count != 0)
                            tempCoordList.Clear();

                        int coordId = 1;
                        string coordStr;
                        while (!string.IsNullOrWhiteSpace(coordStr = _configManager.GetStr(ch, boxSection, $"Team{teamNumber}StartLocation{coordId}")))
                        {
                            if (!MapCoordinate.TryParse(coordStr, out MapCoordinate mapCoordinate))
                            {
                                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Invalid starting location for Match '{matchIdentifier}', Box:{boxNumber}, Team:{teamNumber}, #:{coordId}.");
                                continue;
                            }
                            else
                            {
                                tempCoordList.Add(mapCoordinate);
                            }

                            coordId++;
                        }

                        if (tempCoordList.Count <= 0)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Missing starting location for Match '{matchIdentifier}', Box:{boxNumber}, Team:{teamNumber}.");
                            return false;
                        }

                        boxConfiguration.TeamStartLocations[teamIdx] = tempCoordList.ToArray();
                    }

                    matchConfiguration.Boxes[boxIdx] = boxConfiguration;
                }

                return true;
            }
        }

        private bool MakeMatch(TeamVersusMatchmakingQueue queue)
        {
            if (queue == null)
                return false;

            int startingIndex = queue.MatchConfigurations.Count == 1 ? 0 : _prng.Number(0, queue.MatchConfigurations.Count);
            for (int i = 0; i < queue.MatchConfigurations.Count; i++)
            {
                MatchConfiguration matchConfiguration = queue.MatchConfigurations[(startingIndex + i) % queue.MatchConfigurations.Count];

                //
                // Find the next available location for a game to be played on.
                //

                if (!TryGetAvailableMatch(matchConfiguration, out MatchData matchData))
                    continue;

                //
                // Found a location for a game to be played in. Next, try to find players.
                //

                if (!queue.GetMatch(matchConfiguration, matchData.TeamPlayerSlots))
                    continue;

                //
                // Start the game
                //

                // Reserve the match.
                matchData.State = MatchState.Initializing;

                // Try to find the arena
                Arena arena = _arenaManager.FindArena(matchData.ArenaName); // This will only find the arena if it already exists and is running.

                foreach (PlayerSlot playerSlot in matchData.TeamPlayerSlots)
                {
                    Player player = playerSlot.Player;

                    AssignSlot(playerSlot, player);

                    if (arena != null && player.Arena == arena)
                    {
                        playerSlot.Status = PlayerSlotStatus.Waiting;
                    }
                    else
                    {
                        playerSlot.Status = PlayerSlotStatus.SwitchingArena;
                        _arenaManager.SendToArena(player, matchData.ArenaName, 0, 0);
                    }
                }

                return true;
            }

            return false;

            bool TryGetAvailableMatch(MatchConfiguration matchConfiguration, out MatchData matchData)
            {
                for (int arenaNumber = 0; arenaNumber < matchConfiguration.MaxArenas; arenaNumber++)
                {
                    for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                    {
                        MatchIdentifier matchIdentifier = new(matchConfiguration.MatchType, arenaNumber, boxIdx);
                        if (!_matchDataDictionary.TryGetValue(matchIdentifier, out matchData))
                        {
                            _matchDataDictionary.Add(matchIdentifier, new MatchData(matchIdentifier, matchConfiguration));
                            return true;
                        }

                        if (matchData.State == MatchState.None)
                            return true;
                    }
                }

                // no availablity
                matchData = null;
                return false;
            }
        }

        private void UnassignSlot(PlayerSlot slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.PlayerName))
            {
                _playerSlotDictionary.Remove(slot.PlayerName);
                
                if (slot.Player != null
                    && slot.Player.TryGetExtraData(_pdKey, out PlayerData playerData))
                {
                    playerData.PlayerSlot = null;
                    slot.Player = null;
                }
            }
        }

        private void AssignSlot(PlayerSlot slot, Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            UnassignSlot(slot);

            slot.PlayerName = player.Name;
            slot.Player = player;

            playerData.PlayerSlot = slot;
            _playerSlotDictionary[player.Name] = slot;
        }

        private class MatchConfiguration
        {
            public string MatchType;
            public string QueueName;
            public string ArenaBaseName;
            public int MaxArenas;

            public int NumTeams;
            public int PlayersPerTeam;
            public int LivesPerPlayer;
            public TimeSpan? TimeLimit;
            public TimeSpan? OverTimeLimit;
            public TimeSpan WinConditionDelay;

            public int MaxLagOuts = 3;
            public TimeSpan AllowSubAfter = TimeSpan.FromSeconds(30);

            public MatchBoxConfiguration[] Boxes;
        }

        private class MatchBoxConfiguration
        {
            /// <summary>
            /// Available starting locations for each team.
            /// </summary>
            public MapCoordinate[][] TeamStartLocations;

            public string PlayAreaMapRegion;
        }

        private readonly record struct MatchIdentifier(string MatchType, int ArenaNumber, int BoxIdx) : IEquatable<MatchIdentifier>
        {
            public bool Equals(MatchIdentifier other)
            {
                return string.Equals(MatchType, other.MatchType, StringComparison.OrdinalIgnoreCase)
                    && ArenaNumber == other.ArenaNumber
                    && BoxIdx == other.BoxIdx;
            }

            public override int GetHashCode()
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(MatchType) ^ ArenaNumber.GetHashCode() ^ BoxIdx.GetHashCode();
            }
        }

        private enum MatchState
        {
            /// <summary>
            /// Not currently in use.
            /// In this state, the box can be reserved. 
            /// In all other states, the box is reserved and in use.
            /// </summary>
            None,

            /// <summary>
            /// Gathering players.
            /// </summary>
            Initializing,

            /// <summary>
            /// Ongoing game.
            /// </summary>
            InProgress,

            /// <summary>
            /// The match is complete. A bit of time will be given before the next match.
            /// </summary>
            Complete,
        }

        private class MatchData
        {
            public MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration)
            {
                MatchIdentifier = matchIdentifier;
                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ArenaName = Arena.CreateArenaName(Configuration.ArenaBaseName, MatchIdentifier.ArenaNumber);
                State = MatchState.None;
                TeamPlayerSlots = new PlayerSlot[Configuration.NumTeams, Configuration.PlayersPerTeam];
                for (int teamIdx = 0; teamIdx < Configuration.NumTeams; teamIdx++)
                {
                    for (int slotIdx = 0; slotIdx < Configuration.PlayersPerTeam; slotIdx++)
                    {
                        TeamPlayerSlots[teamIdx, slotIdx] = new PlayerSlot(this, new PlayerSlotIdentifier(teamIdx, slotIdx));
                    }
                }
            }

            public readonly MatchIdentifier MatchIdentifier;
            public readonly MatchConfiguration Configuration;
            public readonly string ArenaName;
            public MatchState State;
            public readonly PlayerSlot[,] TeamPlayerSlots;
        }

        private enum PlayerSlotStatus
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

        private class PlayerSlot
        {
            public readonly MatchData MatchData;
            public readonly PlayerSlotIdentifier Identifier;

            public PlayerSlotStatus Status;

            /// <summary>
            /// The name of current player filling the slot.
            /// </summary>
            public string PlayerName;

            /// <summary>
            /// The current player filling the slot.
            /// This can be null if the player disconnected.
            /// </summary>
            public Player Player;

            /// <summary>
            /// The time the player left.
            /// This will allow us to figure out when the slot can be given to a substibute player.
            /// </summary>
            public DateTime? PlayerLeftTimestamp;

            /// <summary>
            /// Whether the player has requested to be subbed out.
            /// </summary>
            public bool IsSubRequested;

            /// <summary>
            /// The # of times the player has left play.
            /// </summary>
            public int LagOuts;

            /// <summary>
            /// Whether another player can be subbed in to the slot.
            /// </summary>
            public bool CanBeSubbed =>
                IsSubRequested
                || LagOuts >= MatchData.Configuration.MaxLagOuts
                || (PlayerLeftTimestamp != null
                    && (DateTime.UtcNow - PlayerLeftTimestamp.Value) >= MatchData.Configuration.AllowSubAfter);

            /// <summary>
            /// The # of lives remaining.
            /// </summary>
            public int Lives;

            // Info about the current ship/items, so that a sub can be given the same.
            public ShipType Ship;
            public byte Bursts;
            public byte Repels;
            public byte Thors;
            public byte Bricks;
            public byte Decoys;
            public byte Rockets;
            public byte Portals;

            public PlayerSlot(MatchData matchData, PlayerSlotIdentifier identifier)
            {
                MatchData = matchData;
                Identifier = identifier;
            }
        }

        private readonly record struct PlayerSlotIdentifier(int TeamIdx, int SlotIdx);

        private class PlayerData
        {
            /// <summary>
            /// The slot the player is assigned.
            /// <para>
            /// This can be used to determine which match the player is in,
            /// which team the player is assigned,
            /// and which slot of the team the player is assigned.
            /// </para>
            /// </summary>
            public PlayerSlot PlayerSlot;
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

                obj.PlayerSlot = null;

                return true;
            }
        }

        private class TeamVersusMatchmakingQueue : IMatchmakingQueue
        {
            private readonly LinkedList<PlayerOrGroup> _queue = new();

            public TeamVersusMatchmakingQueue(
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

                MatchConfigurations = _matchConfigurationList.AsReadOnly();
            }

            public string Name { get; }
            public QueueOptions Options { get; }
            public string Description { get; }

            private readonly List<MatchConfiguration> _matchConfigurationList = new(1);
            public ReadOnlyCollection<MatchConfiguration> MatchConfigurations { get; }

            public IObjectPoolManager ObjectPoolManager { get; init; }

            public void AddMatchConfiguration(MatchConfiguration matchConfiguration)
            {
                _matchConfigurationList.Add(matchConfiguration);
            }

            public bool Add(Player player)
            {
                _queue.AddLast(new PlayerOrGroup(player));
                return true;
            }

            public bool Add(IPlayerGroup group)
            {
                _queue.AddLast(new PlayerOrGroup(group));
                return true;
            }

            public void GetQueued(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups)
            {
                foreach (PlayerOrGroup pog in _queue)
                {
                    if (pog.Player != null)
                        soloPlayers.Add(pog.Player);
                    else if(pog.Group != null)
                        groups.Add(pog.Group);
                }
            }

            public bool Remove(Player player)
            {
                return _queue.Remove(new PlayerOrGroup(player));
            }

            public bool Remove(IPlayerGroup group)
            {
                return _queue.Remove(new PlayerOrGroup(group));
            }

            public bool GetMatch(MatchConfiguration matchConfiguration, PlayerSlot[,] teamPlayerSlots)
            {
                if (matchConfiguration == null || teamPlayerSlots == null)
                    return false;

                int numTeams = matchConfiguration.NumTeams;
                int playersPerTeam = matchConfiguration.PlayersPerTeam;

                // sanity check
                if (teamPlayerSlots.GetLength(0) != numTeams
                    || teamPlayerSlots.GetLength(1) != playersPerTeam)
                {
                    return false;
                }

                // TODO: logic is simplified when only allowing groups of the exact size, add support for other group sizes later
                if (playersPerTeam != Options.MinGroupSize || playersPerTeam != Options.MaxGroupSize)
                {
                    return false;
                }

                List<LinkedListNode<PlayerOrGroup>> pending = new(); // TODO: object pooling
                List<LinkedListNode<PlayerOrGroup>> pendingSolo = new(); // TODO: object pooling

                try
                {
                    for (int teamIdx = 0; teamIdx < numTeams; teamIdx++)
                    {
                        LinkedListNode<PlayerOrGroup> node = _queue.First;
                        if (node == null)
                            break; // Cannot fill the team.

                        ref PlayerOrGroup pog = ref node.ValueRef;

                        if (pog.Group != null)
                        {
                            // Got a group, which fills the team.
                            Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                            for (int slotIdx = 0; slotIdx < playersPerTeam; slotIdx++)
                                teamPlayerSlots[teamIdx, slotIdx].Player = pog.Group.Members[slotIdx];

                            _queue.Remove(node);
                            pending.Add(node);
                            continue; // Filled the team with a group.
                        }
                        else if (pog.Player != null)
                        {
                            // Got a solo player, check if there are enough solo players to fill the team.
                            pendingSolo.Add(node);

                            while (pendingSolo.Count < playersPerTeam
                                && (node = node.Next) != null)
                            {
                                pog = ref node.ValueRef;
                                if (pog.Player != null)
                                {
                                    pendingSolo.Add(node);
                                }
                            }

                            if (pendingSolo.Count == playersPerTeam)
                            {
                                // Found enough solo players to fill the team.
                                int slotIdx = 0;
                                foreach (LinkedListNode<PlayerOrGroup> soloNode in pendingSolo)
                                {
                                    pog = ref soloNode.ValueRef;
                                    teamPlayerSlots[teamIdx, slotIdx++].Player = pog.Player;
                                    _queue.Remove(soloNode);
                                    pending.Add(soloNode);
                                }

                                pendingSolo.Clear();
                                continue; // Filled the team with solo players.
                            }
                            else
                            {
                                pendingSolo.Clear();

                                // Try to find a group to fill the team instead.
                                node = _queue.First.Next;
                                while (node != null)
                                {
                                    pog = ref node.ValueRef;
                                    if (pog.Group != null)
                                    {
                                        Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                                        for (int slotIdx = 0; slotIdx < playersPerTeam; slotIdx++)
                                            teamPlayerSlots[teamIdx, slotIdx].Player = pog.Group.Members[slotIdx];

                                        _queue.Remove(node);
                                        pending.Add(node);
                                        continue; // Filled the team with a group.
                                    }
                                }

                                break; // Cannot fill the team.
                            }
                        }
                    }

                    bool success = true;
                    foreach (PlayerSlot playerSlot in teamPlayerSlots)
                    {
                        if (playerSlot.Player == null)
                            success = false;
                    }

                    if (success)
                    {
                        return true;
                    }
                    else
                    {
                        // Unable to fill the teams.
                        // Add all the pending nodes back into the queue in their original order.
                        for (int i = pending.Count - 1; i > 0; i--)
                        {
                            LinkedListNode<PlayerOrGroup> node = pending[i];
                            pending.RemoveAt(i);
                            _queue.AddFirst(node);
                        }

                        foreach (PlayerSlot playerSlot in teamPlayerSlots)
                        {
                            playerSlot.Player = null;
                        }

                        return false;
                    }
                }
                finally
                {
                    // TODO: return pooled objects
                    //Return(pending);
                    //Return(pendingSolo);
                }
            }

            /*
            public bool GetMatch(MatchConfiguration matchConfiguration, List<HashSet<Player>> teamPlayerList)
            {
                if (matchConfiguration == null || teamPlayerList == null)
                    return false;

                foreach (HashSet<Player> teamPlayers in teamPlayerList)
                {
                    if (teamPlayers == null)
                        return false;
                }

                int numTeams = teamPlayerList.Count;
                int playersPerTeam = matchConfiguration.PlayersPerTeam;

                // TODO: logic is simplified when only allowing groups of the exact size, add support for other group sizes later
                if (playersPerTeam != Options.MinGroupSize || playersPerTeam != Options.MaxGroupSize)
                {
                    return false;
                }

                List<LinkedListNode<PlayerOrGroup>> pending = new(); // TODO: object pooling
                List<LinkedListNode<PlayerOrGroup>> pendingSolo = new(); // TODO: object pooling

                try
                {
                    foreach (HashSet<Player> teamPlayers in teamPlayerList)
                    {
                        LinkedListNode<PlayerOrGroup> node = _queue.First;
                        if (node == null)
                            break; // Cannot fill the team.

                        ref PlayerOrGroup pog = ref node.ValueRef;

                        if (pog.Group != null)
                        {
                            // Got a group, which fills the team.
                            Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                            teamPlayers.Union(pog.Group.Members);
                            _queue.Remove(node);
                            pending.Add(node);
                            continue; // Filled the team with a group.
                        }
                        else if (pog.Player != null)
                        {
                            // Got a solo player, check if there are enough solo players to fill the team.
                            pendingSolo.Add(node);

                            while (pendingSolo.Count < playersPerTeam
                                && (node = node.Next) != null)
                            {
                                pog = ref node.ValueRef;
                                if (pog.Player != null)
                                {
                                    pendingSolo.Add(node);
                                }
                            }

                            if (pendingSolo.Count == playersPerTeam)
                            {
                                // Found enough solo players to fill the team.
                                foreach (LinkedListNode<PlayerOrGroup> soloNode in pendingSolo)
                                {
                                    pog = ref soloNode.ValueRef;
                                    teamPlayers.Add(pog.Player);
                                    _queue.Remove(soloNode);
                                    pending.Add(soloNode);
                                }

                                pendingSolo.Clear();
                                continue; // Filled the team with solo players.
                            }
                            else
                            {
                                pendingSolo.Clear();

                                // Try to find a group to fill the team instead.
                                node = _queue.First.Next;
                                while (node != null)
                                {
                                    pog = ref node.ValueRef;
                                    if (pog.Group != null)
                                    {
                                        Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                                        teamPlayers.Union(pog.Group.Members);
                                        _queue.Remove(node);
                                        pending.Add(node);
                                        continue; // Filled the team with a group.
                                    }
                                }

                                break; // Cannot fill the team.
                            }
                        }
                    }

                    bool success = true;
                    foreach (HashSet<Player> teamPlayers in teamPlayerList)
                    {
                        if (teamPlayerList.Count != playersPerTeam)
                            success = false;
                    }

                    if (success)
                    {
                        return true;
                    }
                    else
                    {
                        // Unable to fill the teams.
                        // Add all the pending nodes back into the queue in their original order.
                        for (int i = pending.Count - 1; i > 0; i--)
                        {
                            LinkedListNode<PlayerOrGroup> node = pending[i];
                            pending.RemoveAt(i);
                            _queue.AddFirst(node);
                        }

                        return false;
                    }
                }
                finally
                {
                    // TODO: return pooled objects
                    //Return(pending);
                    //Return(pendingSolo);
                }
            }
            */
        }
    }
}
