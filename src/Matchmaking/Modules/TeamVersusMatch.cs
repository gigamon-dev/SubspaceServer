using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.League;
using SS.Matchmaking.Queues;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Replay;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module for team versus matches where the # of teams and # of players per team can be configured.
    /// </summary>
    /// <remarks>
    /// Win conditions:
    /// - last team standing
    /// - a team is eliminated when all its players are knocked out (no remaining lives)
    /// - (alternatively) a team is eliminated if none of its players remain in the play area (respawn area separate from play area), like in 2v2
    /// - match time limit hit and one team has a higher # of lives remaining
    /// - in overtime and a kill is made
    /// 
    /// Subbing (IDEA)
    /// -------
    /// If a player leaves or goes to spec during a game, they have 30 seconds to ?return, after which their spot is forfeit and can be subbed.
    /// Players that leave their game in this manner can be penalized (disallow queuing for a certain timeframe, 10 minutes?).
    /// Alternatively, a player can request to be subbed (no penalization if someone subs).
    /// A player that subs in does not lose their spot in the queue. They can effectively be at the front of the queue when the game ends and get to play in the next game.
    /// 
    /// Time limit
    /// ----------
    /// 30 minutes, 
    /// then 5 minutes of overtime (next kill wins), 
    /// then maybe sudden death - burn items, lower max energy of each player by 1 unit every minute until at the minimum max energy, then lower max recharge rate of each player by 1 every minute
    /// </remarks>
    [ModuleInfo($"""
        Manages team versus matches.
        Configuration: {nameof(TeamVersusMatch)}.conf
        """)]
    public class TeamVersusMatch : IAsyncModule, IMatchmakingQueueAdvisor, IFreqManagerEnforcerAdvisor, IMatchFocusAdvisor, ILeagueGameMode, ILeagueHelp
    {
        private const string ConfigurationFileName = "TeamVersus.conf";

        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly IClientSettings _clientSettings;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapData _mapData;
        private readonly IMatchFocus _matchFocus;
        private readonly IMatchmakingQueues _matchmakingQueues;
        private readonly INetwork _network;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;

        // optional
        private ITeamVersusStatsBehavior? _teamVersusStatsBehavior;
        private ILeagueManager? _leagueManager;
        private IReplayController? _replayController;

        private AdvisorRegistrationToken<IMatchFocusAdvisor>? _iMatchFocusAdvisorToken;
        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor>? _iMatchmakingQueueAdvisorToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private ClientSettingIdentifier _killEnterDelayClientSettingId;
        private readonly ClientSettingIdentifier[] _spawnXClientSettingIds = new ClientSettingIdentifier[4];
        private readonly ClientSettingIdentifier[] _spawnYClientSettingIds = new ClientSettingIdentifier[4];
        private readonly ClientSettingIdentifier[] _spawnRadiusClientSettingIds = new ClientSettingIdentifier[4];

        private TimeSpan _abandonStartPenaltyDuration = TimeSpan.FromMinutes(5);
        private TimeSpan _notReadyStartPenaltyDuration = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Dictionary of queues.
        /// </summary>
        /// <remarks>
        /// key: queue name
        /// </remarks>
        private readonly Dictionary<string, TeamVersusMatchmakingQueue> _queueDictionary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Match configurations by queue.
        /// </summary>
        /// <remarks>
        /// key: queue name
        /// </remarks>
        private readonly Dictionary<string, List<MatchConfiguration>> _queueMatchConfigurations = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Match configurations for league matches by GameTypeId.
        /// </summary>
        /// <remarks>
        /// Key: GameTypeId
        /// </remarks>
        private readonly Dictionary<long, MatchConfiguration> _leagueMatchConfigurations = [];

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
        private readonly Dictionary<MatchIdentifier, MatchData> _matchDataDictionary = [];

        /// <summary>
        /// Dictionary for looking up what slot in a match a player is associated with.
        /// <para>
        /// A player stays associated with a match until the match ends, regardless of if the player left the match or was subbed out.
        /// </para>
        /// </summary>
        /// <remarks>
        /// key: player name
        /// </remarks>
        private readonly Dictionary<string, PlayerSlot> _playerSlotDictionary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Slots that are available for substitute players.
        /// </summary>
        private readonly LinkedList<PlayerSlot> _availableSubSlots = new();

        /// <summary>
        /// Data per arena base name (shared among arenas with the same base name).
        /// Only contains data for arena base names that are configured for matches.
        /// </summary>
        /// <remarks>
        /// Key: Arena base name
        /// </remarks>
        private readonly Dictionary<string, ArenaBaseData> _arenaBaseDataDictionary = [];

        /// <summary>
        /// Data per-arena (not all arenas, only those configured for matches).
        /// </summary>
        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = [];

        private readonly DefaultObjectPool<ArenaData> _arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);
        private readonly DefaultObjectPool<TeamLineup> _teamLineupPool = new(new DefaultPooledObjectPolicy<TeamLineup>(), Constants.TargetPlayerCount);
        private readonly DefaultObjectPool<List<TeamLineup>> _teamLineupListPool = new(new ListPooledObjectPolicy<TeamLineup>(), 8);
        private readonly DefaultObjectPool<List<Player>> _playerListPool = new(new ListPooledObjectPolicy<Player>() { InitialCapacity = Constants.TargetPlayerCount }, 8);

        public TeamVersusMatch(
            IComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            IMatchFocus matchFocus,
            IMatchmakingQueues matchmakingQueues,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _matchFocus = matchFocus ?? throw new ArgumentNullException(nameof(matchFocus));
            _matchmakingQueues = matchmakingQueues ?? throw new ArgumentNullException(nameof(matchmakingQueues));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        }

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _teamVersusStatsBehavior = broker.GetInterface<ITeamVersusStatsBehavior>();
            _leagueManager = broker.GetInterface<ILeagueManager>();
            _replayController = broker.GetInterface<IReplayController>();

            if (!_clientSettings.TryGetSettingsIdentifier("Kill", "EnterDelay", out _killEnterDelayClientSettingId))
            {
                return false;
            }

            if (!GetSpawnClientSettingIdentifiers())
            {
                return false;
            }

            if (!await LoadConfigurationAsync().ConfigureAwait(false))
            {
                return false;
            }

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Register(broker, Callback_MatchmakingQueueChanged);

            _commandManager.AddCommand("loadmatchtype", Command_loadmatchtype);
            _commandManager.AddCommand("unloadmatchtype", Command_unloadmatchtype);

            _iMatchFocusAdvisorToken = broker.RegisterAdvisor<IMatchFocusAdvisor>(this);
            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;

            bool GetSpawnClientSettingIdentifiers()
            {
                Span<char> xKey = stackalloc char["Team#-X".Length];
                Span<char> yKey = stackalloc char["Team#-Y".Length];
                Span<char> rKey = stackalloc char["Team#-Radius".Length];

                "Team#-X".CopyTo(xKey);
                "Team#-Y".CopyTo(yKey);
                "Team#-Radius".CopyTo(rKey);

                for (int i = 0; i < 4; i++)
                {
                    xKey[4] = yKey[4] = rKey[4] = (char)('0' + i);

                    if (!_clientSettings.TryGetSettingsIdentifier("Spawn", xKey, out _spawnXClientSettingIds[i]))
                        return false;

                    if (!_clientSettings.TryGetSettingsIdentifier("Spawn", yKey, out _spawnYClientSettingIds[i]))
                        return false;

                    if (!_clientSettings.TryGetSettingsIdentifier("Spawn", rKey, out _spawnRadiusClientSettingIds[i]))
                        return false;
                }

                return true;
            }
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!broker.UnregisterAdvisor(ref _iMatchFocusAdvisorToken))
                return Task.FromResult(false);

            if (!broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken))
                return Task.FromResult(false);

            _commandManager.RemoveCommand("loadmatchtype", Command_loadmatchtype);
            _commandManager.RemoveCommand("unloadmatchtype", Command_unloadmatchtype);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            _playerData.FreePlayerData(ref _pdKey);

            if (_teamVersusStatsBehavior is not null)
                broker.ReleaseInterface(ref _teamVersusStatsBehavior);

            if (_leagueManager is not null)
                broker.ReleaseInterface(ref _leagueManager);

            if (_replayController is not null)
                broker.ReleaseInterface(ref _replayController);

            return Task.FromResult(true);
        }

        #endregion

        #region IFreqManagerEnforcerAdvsor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return ShipMask.None;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return ShipMask.None;

            bool isPublicPlayFreq = IsPublicPlayFreq(freq);
            if (isPublicPlayFreq && !arenaData.PublicPlayEnabled)
            {
                errorMessage?.Append("This arena does not allow public play.");
                return ShipMask.None;
            }

            MatchData? leagueMatch = arenaData.LeagueMatch;
            if (leagueMatch is not null)
            {
                if (leagueMatch.Status == MatchStatus.Starting)
                {
                    // Don't allow a change at this stage.
                    return player.Ship.GetShipMask();
                }

                if (isPublicPlayFreq)
                {
                    if (leagueMatch.Status == MatchStatus.Initializing)
                    {
                        // Public play is allowed before the match starts.
                        return ShipMask.All;
                    }
                    else
                    {
                        errorMessage?.Append("There is an ongoing league match. Public play is unavailable for the duration.");
                        return ShipMask.None;
                    }
                }

                Team? team = null;
                foreach (Team otherTeam in leagueMatch.Teams)
                {
                    if (otherTeam.Freq == freq)
                    {
                        team = otherTeam;
                        break;
                    }
                }

                if (team is null)
                    return ShipMask.None;

                if (leagueMatch.Status == MatchStatus.Initializing || leagueMatch.Status == MatchStatus.StartingCountdown)
                {
                    // Special rules apply before the match starts since slots are not assigned until it actually starts.
                    return CanPlayAsStarterInLeagueMatch(arena, player, team) ? ShipMask.All : ShipMask.None;
                }
            }

            PlayerSlot? playerSlot = playerData.AssignedSlot;
            if (playerSlot is null)
            {
                // The player is not in a match.
                if (leagueMatch is not null)
                {
                    return ShipMask.None;
                }
                else
                {
                    // Allow changing to any ship for public play.
                    return ShipMask.All;
                }
            }
            else
            {
                // The player is in a match.
                if (CanShipChangeNow(player, playerData))
                {
                    // Set the player's next ship.
                    SetNextShip(player, playerData, ship, false);

                    // Allow changing to any ship.
                    return ShipMask.All;
                }
                else
                {
                    // Not allowed to change ships right now.
                    // Set the player's next ship.
                    SetNextShip(player, playerData, ship, true);

                    // Only allow the current ship. In other words, no change allowed.
                    return player.Ship.GetShipMask();
                }
            }
        }

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder? errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return false;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            bool isPublicPlayFreq = IsPublicPlayFreq(newFreq);
            if (isPublicPlayFreq && !arenaData.PublicPlayEnabled)
            {
                errorMessage?.Append("This arena does not allow public play.");
                return false;
            }

            if (arenaData.LeagueMatch is not null)
            {
                // The arena is reserved for a league match.
                MatchData leagueMatch = arenaData.LeagueMatch;

                if (isPublicPlayFreq)
                {
                    if (leagueMatch.Status == MatchStatus.Initializing)
                    {
                        // Public play is allowed before the match starts.
                        return true;
                    }
                    else
                    {
                        errorMessage?.Append("There is an ongoing league match. Public play is unavailable for the duration.");
                        return false;
                    }
                }
                else
                {
                    foreach (Team team in leagueMatch.Teams)
                    {
                        if (team.Freq == newFreq)
                        {
                            if (team.LeagueTeam is null)
                                continue;

                            // Check that the player is on the team's roster.
                            if (team.LeagueTeam.Roster.ContainsKey(player.Name!))
                            {
                                return true;
                            }
                            else
                            {
                                errorMessage?.Append($"You are not on the roster of: '{team.LeagueTeam.TeamName}'.");
                                return false;
                            }
                        }
                    }

                    errorMessage?.Append($"{newFreq} is not a valid freq for the league match.");
                    return false;
                }
            }
            else
            {
                PlayerSlot? playerSlot = playerData.AssignedSlot;
                if (playerSlot is not null)
                {
                    if (newFreq == playerSlot.Team.Freq)
                    {
                        // In a match, always allow players to change to their team's freq.
                        return true;
                    }

                    if (playerSlot.Status != PlayerSlotStatus.KnockedOut)
                    {
                        // Only team freq is allowed.
                        errorMessage?.Append("Only the team freq is allowed when playing in a match.");
                        return false;
                    }
                }

                if (arenaData.PublicPlayEnabled)
                {
                    if (!isPublicPlayFreq)
                    {
                        errorMessage?.Append("Only frequencies 0-9 are available for public play.");
                    }

                    return isPublicPlayFreq;
                }
                else
                {
                    return false;
                }
            }
        }

        bool IFreqManagerEnforcerAdvisor.CanEnterGame(Player player, StringBuilder? errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return false;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (arenaData.LeagueMatch is not null)
            {
                // The arena is reserved for a league match.
                if (arenaData.LeagueMatch.Status == MatchStatus.Initializing)
                {
                    // The match has not started yet, so special rules apply.
                    if (arenaData.PublicPlayEnabled)
                    {
                        return true;
                    }
                    else
                    {
                        foreach (Team team in arenaData.LeagueMatch.Teams)
                        {
                            if (team.LeagueTeam is null)
                                continue;

                            if (team.LeagueTeam.Roster.ContainsKey(player.Name!))
                                return true;
                        }

                        errorMessage?.Append("You are not on the roster of any team in this league match.");
                        return false;
                    }
                }

                PlayerSlot? playerSlot = playerData.AssignedSlot;
                if (playerSlot is null)
                {
                    return false;
                }
                else
                {
                    if (player.Ship == ShipType.Spec)
                    {
                        // Allow returning to match using regular ship selection, rather than having to use ?return.
                        // This is pretty hacky, as it's trying to fit into the existing FreqManager's use of IFreqManagerEnforcerAdvisor to tie in.
                        ReturnToMatch(player, playerData);
                    }

                    return false;
                }
            }
            else
            {
                PlayerSlot? playerSlot = playerData.AssignedSlot;
                if (playerSlot is null)
                {
                    // Not in a match. Can enter public play if the arena is configured for it.
                    if (!arenaData.PublicPlayEnabled)
                    {
                        errorMessage?.Append($"This arena does not support public play. Use ?{_matchmakingQueues.NextCommandName} to search for a match to play in.");
                    }

                    return arenaData.PublicPlayEnabled;
                }
                else
                {
                    if (player.Ship == ShipType.Spec)
                    {
                        // Allow returning to match using regular ship selection, rather than having to use ?return.
                        // This is pretty hacky, as it's trying to fit into the existing FreqManager's use of IFreqManagerEnforcerAdvisor to tie in.
                        ReturnToMatch(player, playerData);
                    }

                    return false;
                }
            }
        }

        bool IFreqManagerEnforcerAdvisor.IsUnlocked(Player player, StringBuilder? errorMessage)
        {
            return true;
        }

        #endregion

        #region IMatchFocusAdvisor

        bool IMatchFocusAdvisor.TryGetPlaying(IMatch match, HashSet<string> players)
        {
            if (match is null || match is not MatchData matchData)
                return false;

            foreach (var team in matchData.Teams)
            {
                foreach (var slot in team.Slots)
                {
                    if (slot.PlayerName is not null)
                    {
                        players.Add(slot.PlayerName);
                    }
                }
            }

            return true;
        }

        IMatch? IMatchFocusAdvisor.GetMatch(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return null;

            return playerData.AssignedSlot?.MatchData;
        }

        #endregion

        #region ILeagueGameMode

        ILeagueMatch? ILeagueGameMode.CreateMatch(LeagueGameInfo leagueGame)
        {
            if (!_leagueMatchConfigurations.TryGetValue(leagueGame.GameTypeId, out MatchConfiguration? matchConfiguration))
                return null;

            if (!TryGetAvailableMatch(matchConfiguration, leagueGame, out MatchData? matchData))
                return null;

            //
            // Reserve the match.
            //

            matchData.Status = MatchStatus.Initializing;

            // Send a chat notification to the entire zone.
            StringBuilder teamsBuilder = _objectPoolManager.StringBuilderPool.Get();
            StringBuilder announcementBuilder = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                foreach (var team in leagueGame.Teams.Values)
                {
                    if (teamsBuilder.Length > 0)
                        teamsBuilder.Append(" vs ");

                    teamsBuilder.Append(team.TeamName);
                }

                announcementBuilder.Append($"{leagueGame.LeagueName} - {leagueGame.SeasonName}: ");
                announcementBuilder.Append(teamsBuilder);
                announcementBuilder.Append($" is starting in ?go {matchData.ArenaName}");

                _chat.SendArenaMessage(null, announcementBuilder);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(teamsBuilder);
                _objectPoolManager.StringBuilderPool.Return(announcementBuilder);
            }

            Arena? arena = matchData.Arena;
            if (arena is not null)
            {
                // Move everyone in the arena to spec (freq and ship).
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena)
                        continue;

                    bool changeFreq = player.Freq != arena.SpecFreq;
                    bool changeShip = player.Ship != ShipType.Spec;

                    if (changeFreq)
                    {
                        if (changeShip)
                            _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                        else
                            _game.SetFreq(player, arena.SpecFreq);
                    }
                    else if (changeShip)
                    {
                        _game.SetShip(player, ShipType.Spec);
                    }
                }
            }

            return matchData;
        }

        #endregion

        #region ILeagueHelp

        void ILeagueHelp.PrintHelp(Player player)
        {
            _chat.SendMessage(player, "--- Match Information ---------------------------------------------------------");
            PrintCommand(player, CommandNames.MatchInfo, "Prints information about the current match.");
            PrintCommand(player, CommandNames.Rosters, "Prints the team rosters of the current match.");
            _chat.SendMessage(player, "--- Team Members --------------------------------------------------------------");
            PrintCommand(player, CommandNames.RequestSub, "Makes your slot available for subbing.");
            PrintCommand(player, CommandNames.Sub, "To fill an empty slot or to sub in for another player.");
            PrintCommand(player, CommandNames.CancelSub, "To cancel an ongoing attempt to sub in for another player.");
            PrintCommand(player, CommandNames.Cap, "To take captain powers for your the freq you are on.");
            _chat.SendMessage(player, "--- Captains ------------------------------------------------------------------");
            PrintCommand(player, CommandNames.Ready, "Toggles the indicator on whether your team is ready.");
            PrintCommand(player, CommandNames.AllowPlay, "Toggles starters. Who can play (in a ship) before GO.");
            PrintCommand(player, CommandNames.AllowSub, "Toggles whether a player can ?sub in after GO.");
            PrintCommand(player, CommandNames.FullSub, "Toggles whether a slot is enabled for a full sub.");
            PrintCommand(player, CommandNames.RequestSub, "Makes the targeted slot available for subbing.");

            if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(player, "--- Staff ---------------------------------------------------------------------");
                PrintCommand(player, CommandNames.ForceStart, "Forces a league match to start.");
            }

            void PrintCommand(Player player, string command, string description)
            {
                _chat.SendMessage(player, $"?{command,-10}  {description}");
            }
        }

        #endregion

        #region Callbacks

        [ConfigHelp<bool>("SS.Matchmaking.TeamVersusMatch", "PublicPlayEnabled", ConfigScope.Arena, Default = false,
            Description = "Whether to allow players into ships without being in a match.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            bool isRegisteredArena = false;
            foreach (MatchConfiguration configuration in _matchConfigurationDictionary.Values)
            {
                if (string.Equals(arena.BaseName, configuration.ArenaBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    isRegisteredArena = true;
                    break;
                }
            }

            if (!isRegisteredArena)
                return;

            if (action == ArenaAction.Create)
            {
                ConfigHandle ch = arena.Cfg!;
                ArenaData arenaData = _arenaDataPool.Get();
                _arenaDataDictionary.Add(arena, arenaData);

                // Read ship settings from the config.
                string[] shipNames = Enum.GetNames<ShipType>();
                for (int i = 0; i < 8; i++)
                {
                    arenaData.ShipSettings[i] = new ShipSettings()
                    {
                        InitialBurst = (byte)_configManager.GetInt(ch, shipNames[i], "InitialBurst", 0),
                        InitialRepel = (byte)_configManager.GetInt(ch, shipNames[i], "InitialRepel", 0),
                        InitialThor = (byte)_configManager.GetInt(ch, shipNames[i], "InitialThor", 0),
                        InitialBrick = (byte)_configManager.GetInt(ch, shipNames[i], "InitialBrick", 0),
                        InitialDecoy = (byte)_configManager.GetInt(ch, shipNames[i], "InitialDecoy", 0),
                        InitialRocket = (byte)_configManager.GetInt(ch, shipNames[i], "InitialRocket", 0),
                        InitialPortal = (byte)_configManager.GetInt(ch, shipNames[i], "InitialPortal", 0),
                        MaximumEnergy = (short)_configManager.GetInt(ch, shipNames[i], "MaximumEnergy", 0),
                    };
                }

                arenaData.PublicPlayEnabled = _configManager.GetBool(ch, "SS.Matchmaking.TeamVersusMatch", "PublicPlayEnabled", false);

                // Register callbacks.
                KillCallback.Register(arena, Callback_Kill);
                PreShipFreqChangeCallback.Register(arena, Callback_PreShipFreqChange);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
                PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
                BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
                SpawnCallback.Register(arena, Callback_Spawn);

                // Register commands.
                _commandManager.AddCommand(CommandNames.RequestSub, Command_requestsub, arena);
                _commandManager.AddCommand(CommandNames.Sub, Command_sub, arena);
                _commandManager.AddCommand(CommandNames.CancelSub, Command_cancelsub, arena);
                _commandManager.AddCommand(CommandNames.Return, Command_return, arena);
                //_commandManager.AddCommand(CommandNames.Restart, Command_restart, arena);
                //_commandManager.AddCommand(CommandNames.Randomize, Command_randomize, arena);
                //_commandManager.AddCommand(CommandNames.End, Command_end, arena);
                //_commandManager.AddCommand(CommandNames.Draw, Command_draw, arena);
                _commandManager.AddCommand(CommandNames.ShipChange, Command_sc, arena);
                _commandManager.AddCommand(CommandNames.Items, Command_items, arena);
                _commandManager.AddCommand(CommandNames.MatchInfo, Command_matchinfo, arena);
                _commandManager.AddCommand(CommandNames.FreqInfo, Command_matchinfo, arena); // alias of ?matchinfo since existing 4v4 players accustomed to !freqinfo
                _commandManager.AddCommand(CommandNames.Rosters, Command_rosters, arena);

                _commandManager.AddCommand(CommandNames.Cap, Command_cap, arena);
                _commandManager.AddCommand(CommandNames.Ready, Command_ready, arena);
                _commandManager.AddCommand(CommandNames.Rdy, Command_ready, arena);
                _commandManager.AddCommand(CommandNames.ForceStart, Command_forcestart, arena);
                _commandManager.AddCommand(CommandNames.AllowPlay, Command_allowplay, arena);
                _commandManager.AddCommand(CommandNames.AllowSub, Command_allowsub, arena);
                _commandManager.AddCommand(CommandNames.FullSub, Command_fullsub, arena);

                // Register advisors and interfaces.
                arenaData.IFreqManagerEnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
                arenaData.ILeagueHelpToken = arena.RegisterInterface<ILeagueHelp>(this, nameof(TeamVersusMatch));

                // Fill in arena for associated matches.
                foreach (MatchData matchData in _matchDataDictionary.Values)
                {
                    if (matchData.Arena is null && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchData.Arena = arena;

                        // Also keep track of league matches in the ArenaData.
                        if (matchData.LeagueGame is not null)
                            arenaData.LeagueMatch = matchData;
                    }
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!_arenaDataDictionary.Remove(arena, out ArenaData? arenaData))
                    return;

                try
                {
                    if (!string.IsNullOrWhiteSpace(arenaData.ReplayRecordingFilePath))
                    {
                        StopRecordingReplay(arena, arenaData);
                    }

                    // Unregister advisors and interfaces
                    if (!arena.UnregisterAdvisor(ref arenaData.IFreqManagerEnforcerAdvisorToken))
                        return;

                    if (arena.UnregisterInterface(ref arenaData.ILeagueHelpToken) != 0)
                        return;

                    // Unregister callbacks.
                    KillCallback.Unregister(arena, Callback_Kill);
                    PreShipFreqChangeCallback.Unregister(arena, Callback_PreShipFreqChange);
                    ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
                    PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
                    BricksPlacedCallback.Unregister(arena, Callback_BricksPlaced);
                    SpawnCallback.Unregister(arena, Callback_Spawn);

                    // Unregister commands.
                    _commandManager.RemoveCommand(CommandNames.RequestSub, Command_requestsub, arena);
                    _commandManager.RemoveCommand(CommandNames.Sub, Command_sub, arena);
                    _commandManager.RemoveCommand(CommandNames.CancelSub, Command_cancelsub, arena);
                    _commandManager.RemoveCommand(CommandNames.Return, Command_return, arena);
                    //_commandManager.RemoveCommand(CommandNames.Restart, Command_restart, arena);
                    //_commandManager.RemoveCommand(CommandNames.Randomize, Command_randomize, arena);
                    //_commandManager.RemoveCommand(CommandNames.End, Command_end, arena);
                    //_commandManager.RemoveCommand(CommandNames.Draw, Command_draw, arena);
                    _commandManager.RemoveCommand(CommandNames.ShipChange, Command_sc, arena);
                    _commandManager.RemoveCommand(CommandNames.Items, Command_items, arena);
                    _commandManager.RemoveCommand(CommandNames.MatchInfo, Command_matchinfo, arena);
                    _commandManager.RemoveCommand(CommandNames.FreqInfo, Command_matchinfo, arena);
                    _commandManager.RemoveCommand(CommandNames.Rosters, Command_rosters, arena);

                    _commandManager.RemoveCommand(CommandNames.Cap, Command_cap, arena);
                    _commandManager.RemoveCommand(CommandNames.Ready, Command_ready, arena);
                    _commandManager.RemoveCommand(CommandNames.Rdy, Command_ready, arena);
                    _commandManager.RemoveCommand(CommandNames.ForceStart, Command_forcestart, arena);
                    _commandManager.RemoveCommand(CommandNames.AllowPlay, Command_allowplay, arena);
                    _commandManager.RemoveCommand(CommandNames.AllowSub, Command_allowsub, arena);
                    _commandManager.RemoveCommand(CommandNames.FullSub, Command_fullsub, arena);

                }
                finally
                {
                    _arenaDataPool.Return(arenaData);
                }

                // Clear arena for associated matches.
                foreach (MatchData matchData in _matchDataDictionary.Values)
                {
                    if (matchData.Arena == arena)
                    {
                        matchData.Arena = null;
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (action == PlayerAction.Connect)
            {
                // This flag tells us that the player just connected.
                // We'll use it when the player enters the game.
                playerData.IsInitialConnect = true;
            }
            else if (action == PlayerAction.EnterArena)
            {
                playerData.IsInMatchArena = playerData.AssignedSlot is not null && playerData.AssignedSlot.MatchData.Arena == arena;
            }
            else if (action == PlayerAction.EnterGame)
            {
                if (playerData.IsInitialConnect)
                {
                    // The player connected and entered an arena.
                    playerData.IsInitialConnect = false;

                    if (_playerSlotDictionary.TryGetValue(player.Name!, out PlayerSlot? playerSlot))
                    {
                        // The player is associated with an ongoing match.
                        if (string.Equals(playerSlot.PlayerName, player.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // The player is still assigned to the slot.
                            playerData.AssignedSlot = playerSlot;
                            playerSlot.Player = player;
                        }

                        if (!string.Equals(arena!.Name, playerSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                        {
                            // The arena the player entered is not the arena their match is in. Send them to the proper arena.
                            _chat.SendMessage(player, "Sending you to your ongoing match's arena. Please stand by...");
                            _arenaManager.SendToArena(player, playerSlot.MatchData.ArenaName, 0, 0);
                            return;
                        }
                    }
                }

                playerData.HasFullyEnteredArena = true;

                PlayerSlot? slot = playerData.AssignedSlot;
                if (slot is not null)
                {
                    MatchData matchData = slot.MatchData;

                    if (IsStartingPhase(matchData.Status) && arena == matchData.Arena)
                    {
                        // The player entered the designated arena for a match they're in that's starting up.
                        ProcessMatchStateChange(slot.MatchData);
                    }
                    else if (playerData.IsReturning
                        && string.Equals(arena!.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        // The player re-entered an arena for a match they're trying to ?return to.
                        playerData.IsReturning = false;
                        ReturnToMatch(player, playerData);
                    }
                    else if (CanReturnToMatch(player, playerData, false))
                    {
                        _chat.SendMessage(player, $"Use ?{CommandNames.Return} to return to you match.");
                    }
                }
                else if (playerData.SubSlot is not null
                    && string.Equals(arena!.Name, playerData.SubSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    // The player entered an arena for a match that they're trying to ?sub in for.
                    SubSlot(playerData.SubSlot);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                playerData.HasFullyEnteredArena = false;

                if (playerData.IsWatchingExtraPositionData)
                {
                    _game.RemoveExtraPositionDataWatch(player);
                    playerData.IsWatchingExtraPositionData = false;
                }

                if (playerData.SubSlot is not null
                    && string.Equals(arena!.Name, playerData.SubSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    // The player left the arena before entering the game, and therefore did not complete subbing into the match.
                    CancelSubInProgress(playerData.SubSlot, false);
                }

                PlayerSlot? slot = playerData.AssignedSlot;
                if (slot is not null
                    && string.Equals(arena!.Name, slot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    MatchData matchData = slot.MatchData;

                    if (IsStartingPhase(matchData.Status))
                    {
                        if (playerData.IsInMatchArena)
                        {
                            slot.HasLeftMatchArena = true;
                        }

                        ProcessMatchStateChange(matchData);
                    }

                    if (matchData.Status == MatchStatus.InProgress
                        && slot.Status == PlayerSlotStatus.Playing)
                    {
                        // The player left the arena while playing in a match.
                        SetSlotInactive(slot, SlotInactiveReason.LeftArena);
                    }
                }

                if (arena is not null 
                    && _arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData)
                    && arenaData.LeagueMatch is not null)
                {
                    foreach (Team team in arenaData.LeagueMatch.Teams)
                    {
                        if (team.Captain == player)
                        {
                            team.Captain = null;
                            _chat.SendArenaMessage(arena, $"{player.Name} is no longer the captain of freq {team.Freq} ({team.LeagueTeam?.TeamName})");
                        }
                    }
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (playerData.IsWatchingExtraPositionData)
                {
                    _game.RemoveExtraPositionDataWatch(player);
                    playerData.IsWatchingExtraPositionData = false;
                }

                if (playerData.SubSlot is not null)
                {
                    // The player disconnected from the server before being able to complete subbing into the match.
                    CancelSubInProgress(playerData.SubSlot, false);
                }

                PlayerSlot? slot = playerData.AssignedSlot;
                if (slot is null)
                    return;

                // The player stays assigned to the slot.
                // However, the player object is no longer available to us.
                slot.Player = null;

                // The PlayerData will get cleared and returned to the pool.

                MatchData matchData = slot.MatchData;
                if (matchData.Status == MatchStatus.Initializing
                    || matchData.Status == MatchStatus.StartingCheck
                    || matchData.Status == MatchStatus.StartingCountdown)
                {
                    // The player left a match that was initializing/starting.
                    ProcessMatchStateChange(matchData);
                }
            }
        }

        private void Callback_MatchmakingQueueChanged(IMatchmakingQueue queue, QueueAction action)
        {
            if (action != QueueAction.Add
                || !_queueDictionary.TryGetValue(queue.Name, out TeamVersusMatchmakingQueue? found)
                || found != queue)
            {
                return;
            }

            // TODO: if there's an ongoing game that needs a sub, try to get one for it?

            // Check if a new match can be started.
            while (MakeMatch(found)) { }
        }

        private async void Callback_Kill(Arena arena, Player? killer, Player? killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!killed!.TryGetExtraData(_pdKey, out PlayerData? killedPlayerData))
                return;

            if (!killer!.TryGetExtraData(_pdKey, out PlayerData? killerPlayerData))
                return;

            PlayerSlot? killedPlayerSlot = killedPlayerData.AssignedSlot;
            if (killedPlayerSlot is null)
                return;

            PlayerSlot? killerPlayerSlot = killerPlayerData.AssignedSlot;
            if (killerPlayerSlot is null)
                return;

            MatchData matchData = killedPlayerSlot.MatchData;
            if (matchData != killerPlayerSlot.MatchData)
                return;

            if (matchData.Status != MatchStatus.InProgress)
                return;

            ServerTick nowTick = ServerTick.Now;
            DateTime now = DateTime.UtcNow;

            killedPlayerSlot.Lives--;

            if (matchData.Configuration.DeathSubDuration > TimeSpan.Zero)
            {
                killedPlayerSlot.LastDeathForDeathSub = now;

                // DeathSubs are enabled, set the expiration.
                killedPlayerSlot.DeathSubExpiration = now + matchData.Configuration.DeathSubDuration;
            }

            if (killerPlayerSlot.Team != killedPlayerSlot.Team)
            {
                killerPlayerSlot.Team.Score++;
            }
            else if (killerPlayerSlot.Team == killedPlayerSlot.Team // TK
                && matchData.Configuration.NumTeams == 2)
            {
                // Special logic only for matches with 2 teams.
                // Give a point to the other team for TKs.
                Team otherTeam = matchData.Teams[0] != killedPlayerSlot.Team ? matchData.Teams[0] : matchData.Teams[1];
                otherTeam.Score++;
            }

            bool isKnockout = killedPlayerSlot.Lives <= 0;

            if (isKnockout)
            {
                // This was the last life of the slot.
                killedPlayerSlot.Status = PlayerSlotStatus.KnockedOut;

                // If there was a sub in-progress, cancel it.
                if (killedPlayerSlot.SubPlayer is not null)
                {
                    CancelSubInProgress(killedPlayerSlot, true);
                }

                // If the slot was available for a sub (e.g. ?requestsub), it no longer is.
                killedPlayerSlot.AvailableSubSlotNode.List?.Remove(killedPlayerSlot.AvailableSubSlotNode);
                killedPlayerSlot.IsSubRequested = false;

                // The player needs to be moved to spec, but if done immediately, any remaining weapons fire from the player will be removed.
                // We want to wait a short time to allow for a double-kill (though shorter than the respawn time),
                // before moving the player to spec.

                short enterDelayTicks =
                    _clientSettings.TryGetSettingOverride(killed, _killEnterDelayClientSettingId, out int enterDelayInt)
                    ? (short)enterDelayInt
                    : _clientSettings.TryGetSettingOverride(arena, _killEnterDelayClientSettingId, out enterDelayInt)
                        ? (short)enterDelayInt
                        : (short)_clientSettings.GetSetting(arena, _killEnterDelayClientSettingId);

                TimeSpan enterDelay = TimeSpan.FromMilliseconds(enterDelayTicks * 10);
                TimeSpan koDelay = matchData.Configuration.WinConditionDelay < enterDelay
                    ? matchData.Configuration.WinConditionDelay
                    : enterDelay / 2;

                _mainloopTimer.SetTimer(MainloopTimer_ProcessKnockOut, (int)koDelay.TotalMilliseconds, Timeout.Infinite, killedPlayerSlot, matchData);
            }

            // Regardless of if it was a knockout, we need to check for a win condition.
            // We don't want to do the check immediately, since there's a chance for a double kill.
            // Schedule the win condition check to get executed later.
            ScheduleWinConditionCheckForMatchCompletion(matchData);

            TeamVersusMatchPlayerKilledCallback.Fire(arena, killedPlayerSlot, killerPlayerSlot, isKnockout);

            // Allow the stats module to send chat message notifications about the kill.
            // The stats module can calculate assists and solo kills based on damage stats, and write a more detailed chat message than we can in here.
            bool isNotificationHandled = false;

            string killedName = killed.Name!;
            string killerName = killer.Name!;

            if (_teamVersusStatsBehavior is not null)
            {
                isNotificationHandled = await _teamVersusStatsBehavior.PlayerKilledAsync(
                    nowTick,
                    now,
                    matchData,
                    killed,
                    killedPlayerSlot,
                    killer,
                    killerPlayerSlot,
                    isKnockout);

                // The Player objects (and therefore the PlayerData objects too) might be invalid after the await
                // (e.g. if a player disconnected during the delay).
                // We could verify a Player object by comparing Player.Name and check that Player.Status = PlayerState.Playing.
                // However, we aren't going to use the Player or PlayerData objects after this point.
                // So, let's just clear our references to them.
                killed = null;
                killedPlayerData = null;
                killer = null;
                killerPlayerData = null;
            }

            if (!isNotificationHandled)
            {
                // Notifications were not sent by the stats module.
                // Either the stats is not loaded (it's optional), or it ran into an issue.
                // This means we have to send basic notifications ourself.

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    TimeSpan gameTime = matchData.Started is not null ? now - matchData.Started.Value : TimeSpan.Zero;

                    GetPlayersToNotify(matchData, notifySet);

                    // Kill notification
                    _chat.SendSetMessage(notifySet, $"{killedName} kb {killerName}");

                    // Remaining lives notification
                    if (isKnockout)
                    {
                        sb.Append($"{killedName} is OUT!");
                    }
                    else
                    {
                        sb.Append($"{killedName} has {killedPlayerSlot.Lives} {(killedPlayerSlot.Lives > 1 ? "lives" : "life")} remaining");
                    }

                    sb.Append(" [");
                    sb.AppendFriendlyTimeSpan(gameTime);
                    sb.Append(']');

                    _chat.SendSetMessage(notifySet, sb);
                    sb.Clear();

                    // Score notification
                    sb.Append("Score: ");

                    StringBuilder remainingBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        short highScore = -1;
                        short highScoreFreq = -1;
                        int highScoreCount = 0;

                        foreach (var team in matchData.Teams)
                        {
                            if (sb.Length > "Score: ".Length)
                            {
                                sb.Append('-');
                                remainingBuilder.Append('v');
                            }

                            int remainingSlots = 0;
                            foreach (var slot in team.Slots)
                            {
                                if (slot.Lives > 0)
                                {
                                    remainingSlots++;
                                }
                            }

                            sb.Append(team.Score);
                            remainingBuilder.Append(remainingSlots);

                            if (team.Score > highScore)
                            {
                                highScore = team.Score;
                                highScoreFreq = team.Freq;
                                highScoreCount = 1;
                            }
                            else if (team.Score == highScore)
                            {
                                highScoreCount++;
                                highScoreFreq = -1;
                            }
                        }

                        if (highScoreCount == 1)
                        {
                            sb.Append($" Freq {highScoreFreq}");
                        }
                        else
                        {
                            sb.Append(" TIE");
                        }

                        sb.Append(" -- ");
                        sb.Append(remainingBuilder);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(remainingBuilder);
                    }

                    sb.Append(" -- [");
                    sb.AppendFriendlyTimeSpan(gameTime);
                    sb.Append(']');

                    _chat.SendSetMessage(notifySet, sb);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifySet);
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }


            bool MainloopTimer_ProcessKnockOut(PlayerSlot slot)
            {
                if (slot.Player is not null && slot.Player.Ship != ShipType.Spec && slot.Player.Freq == slot.Team.Freq)
                {
                    _game.SetShip(slot.Player, ShipType.Spec);
                }

                return false;
            }
        }

        // This is called synchronously when the Game module sets a player's ship/freq.
        private void Callback_PreShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            MatchData match = slot.MatchData;

            if (arena != match.Arena || newFreq != slot.Team.Freq)
                return;

            ShipType slotOldShip = slot.Ship;

            if (newShip != ShipType.Spec)
            {
                // Keep track of the ship for the slot, for when a player wants to ?return or ?sub in.
                slot.Ship = newShip;

                if (match.Status == MatchStatus.InProgress)
                {
                    // Adjust items, if needed.
                    AdjustItems(slot, slot.ShipChangeItemsAction);
                }
            }

            if (match.Status == MatchStatus.InProgress
                && slotOldShip != ShipType.Spec
                && slot.Ship != ShipType.Spec
                && slotOldShip != slot.Ship)
            {
                AllowShipChangeReason? reason = slot.AllowShipChangeReason;

                if (slot.AllowShipChangeExpiration is not null && ++slot.ShipChangeCount >= match.Configuration.MaxShipChangesPerEligibleEvent)
                {
                    // Used up the allowed # of ship changes.
                    slot.AllowShipChangeExpiration = null;
                    slot.AllowShipChangeReason = null;
                }

                // Send a ship change notification.
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(match, players);
                    players.Add(player);

                    StringBuilder details = _objectPoolManager.StringBuilderPool.Get();
                    StringBuilder notification = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        // Details - reason
                        if (reason is not null)
                        {
                            switch (reason.Value)
                            {
                                case AllowShipChangeReason.FillUnused:
                                    details.Append("After Filling Unused Slot");
                                    break;
                                case AllowShipChangeReason.Death:
                                    details.Append("After Death");
                                    break;
                                case AllowShipChangeReason.Sub:
                                    details.Append("After Sub");
                                    break;
                                default:
                                    break;
                            }
                        }

                        // Details - items
                        string itemsActionDescription = GetItemsActionDescription(slot.ShipChangeItemsAction);
                        if (!string.IsNullOrEmpty(itemsActionDescription))
                        {
                            if (details.Length > 0)
                                details.Append(": ");

                            details.Append(itemsActionDescription);
                        }

                        // Notification
                        notification.Append($"{slot.Player!.Name} changed to a {slot.Player.Ship}");

                        if (details.Length > 0)
                        {
                            notification.Append($" [");
                            notification.Append(details);
                            notification.Append(']');
                        }

                        _chat.SendSetMessage(players, notification);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(details);
                        _objectPoolManager.StringBuilderPool.Return(notification);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }

                TeamVersusMatchPlayerShipChangedCallback.Fire(arena, slot, slotOldShip, slot.Ship);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            // Spawn position overrides
            if (newShip == ShipType.Spec && playerData.IsSpawnOverriden)
            {
                if (UnoverrideSpawnSettings(player, playerData))
                    _clientSettings.SendClientSettings(player);
            }

            // Extra position data (for tracking items)
            if (newShip != ShipType.Spec
                && !playerData.IsWatchingExtraPositionData)
            {
                _game.AddExtraPositionDataWatch(player);
                playerData.IsWatchingExtraPositionData = true;
            }
            else if (newShip == ShipType.Spec
                && playerData.IsWatchingExtraPositionData)
            {
                _game.RemoveExtraPositionDataWatch(player);
                playerData.IsWatchingExtraPositionData = false;
            }

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            if (slot.MatchData.Status == MatchStatus.InProgress
                && slot.Status == PlayerSlotStatus.Playing
                && newShip == ShipType.Spec)
            {
                SetSlotInactive(slot, SlotInactiveReason.ChangedToSpec);
            }
        }

        private void Callback_PlayerPositionPacket(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (player.Ship == ShipType.Spec)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null || slot.Status != PlayerSlotStatus.Playing)
                return;

            if (hasExtraPositionData)
            {
                // Keep track of the items for the slot.
                ItemChanges changes = ItemChanges.None;
                ref ItemInventory items = ref slot.Items;

                if (items.Bursts != extra.Bursts)
                {
                    items.Bursts = extra.Bursts;
                    changes |= ItemChanges.Bursts;
                }

                if (items.Repels != extra.Repels)
                {
                    items.Repels = extra.Repels;
                    changes |= ItemChanges.Repels;
                }

                if (items.Thors != extra.Thors)
                {
                    items.Thors = extra.Thors;
                    changes |= ItemChanges.Thors;
                }

                if (items.Bricks != extra.Bricks)
                {
                    items.Bricks = extra.Bricks;
                    changes |= ItemChanges.Bricks;
                }

                if (items.Decoys != extra.Decoys)
                {
                    items.Decoys = extra.Decoys; 
                    changes |= ItemChanges.Decoys;
                }

                if (items.Rockets != extra.Rockets)
                {
                    items.Rockets = extra.Rockets; 
                    changes |= ItemChanges.Rockets;
                }

                if (items.Portals != extra.Portals)
                {
                    items.Portals = extra.Portals;
                    changes |= ItemChanges.Portals;
                }

                if (changes != ItemChanges.None)
                {
                    TeamVersusMatchPlayerItemsChangedCallback.Fire(arena, slot, changes);
                }
            }

            if (positionPacket.Weapon.Type != WeaponCodes.Null // Note: bricks are not position packet weapons, therefore handled separately with Callback_BricksPlaced
                && slot.AllowShipChangeExpiration is not null)
            {
                // The player has engaged.
                slot.AllowShipChangeExpiration = null;
            }

            MatchData matchData = slot.MatchData;
            if (matchData.Status == MatchStatus.StartingCheck && !playerData.IsReadyToStart)
            {
                bool isReady = false;

                if (positionPacket.Weapon.Type != WeaponCodes.Null)
                {
                    // The player fired a weapon.
                    isReady = true;
                }
                else
                {
                    // Watch for a change in ship rotation.

                    if (positionPacket.Status == PlayerPositionStatus.Flash)
                    {
                        // The player warped. It doesn't mean the player is ready.
                        // It could be due to external factors (door, brick, wormhole).
                        // When warped, the player's rotation can change, so reset the rotation check.
                        playerData.LastRotation = null;
                    }
                    else
                    {
                        if (playerData.LastRotation is null)
                        {
                            playerData.LastRotation = positionPacket.Rotation;
                        }
                        else if (playerData.LastRotation.Value != positionPacket.Rotation)
                        {
                            // The player's rotation changed.
                            isReady = true;
                        }
                    }
                }

                if (isReady)
                {
                    playerData.IsReadyToStart = true;
                    ProcessMatchStateChange(matchData);
                }
            }

            string? playAreaRegion = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].PlayAreaMapRegion;
            if (string.IsNullOrEmpty(playAreaRegion))
            {
                slot.IsInPlayArea = true;
            }
            else
            {
                if (positionPacket.X == 0 && positionPacket.Y == 0)
                {
                    // TODO: Verify this. When a player dies, it might send these coordinates? In which case, just ignore it.
                }
                else
                {
                    bool isInPlayArea = false;

                    foreach (var region in _mapData.RegionsAt(arena, (short)(positionPacket.X >> 4), (short)(positionPacket.Y >> 4)))
                    {
                        if (string.Equals(region.Name, playAreaRegion, StringComparison.OrdinalIgnoreCase))
                        {
                            isInPlayArea = true;
                            break;
                        }
                    }

                    bool changed = slot.IsInPlayArea != isInPlayArea;
                    if (changed)
                    {
                        slot.IsInPlayArea = isInPlayArea;

                        if (!slot.IsInPlayArea)
                        {
                            // The player is outside of the play area.
                            ScheduleWinConditionCheckForMatchCompletion(matchData);
                        }
                    }
                }
            }

            slot.IsFullEnergy =
                _arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData)
                && positionPacket.Energy >= arenaData.ShipSettings[(int)player.Ship].MaximumEnergy;

            if (slot.SubPlayer is not null && slot.IsFullEnergy)
            {
                // Another player is waiting to sub in. Try to sub the other player in.
                SubSlot(slot);
            }
        }

        private void Callback_BricksPlaced(Arena arena, Player? player, IReadOnlyList<BrickData> bricks)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null || slot.Status != PlayerSlotStatus.Playing)
                return;

            if (slot.AllowShipChangeExpiration is not null)
            {
                // The player has engaged.
                slot.AllowShipChangeExpiration = null;
            }
        }

        private void Callback_Spawn(Player player, SpawnCallback.SpawnReason reasons)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null
                || slot.Status != PlayerSlotStatus.Playing
                || player.Arena is null
                || player.Arena != slot.MatchData.Arena
                || player.Freq != slot.Team.Freq)
            {
                return;
            }

            bool isAfterDeath = (reasons & SpawnCallback.SpawnReason.AfterDeath) == SpawnCallback.SpawnReason.AfterDeath;
            bool isShipChange = (reasons & SpawnCallback.SpawnReason.ShipChange) == SpawnCallback.SpawnReason.ShipChange;
            bool isInitial = (reasons & SpawnCallback.SpawnReason.Initial) == SpawnCallback.SpawnReason.Initial;

            MatchData matchData = slot.MatchData;
            if (isShipChange && !isInitial && IsStartingPhase(matchData.Status))
            {
                // Return the player to their team's starting location.
                Team team = slot.Team;
                TileCoordinates startLocation = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[team.TeamIdx][team.StartLocationIdx];
                _game.WarpTo(player, startLocation.X, startLocation.Y);
            }

            if (isAfterDeath)
            {
                // Allow the player to ship change after respawning from a death for a limited amount of time.
                TimeSpan allowShipChangeDuration = slot.MatchData.Configuration.AllowShipChangeAfterDeathDuration;
                slot.AllowShipChangeExpiration = allowShipChangeDuration > TimeSpan.Zero
                    ? DateTime.UtcNow + allowShipChangeDuration
                    : null;
                slot.AllowShipChangeReason = slot.AllowShipChangeExpiration is not null ? AllowShipChangeReason.Death : null;
                slot.ShipChangeItemsAction = ItemsAction.Full;
                slot.ShipChangeCount = 0;

                if (!isShipChange
                    && playerData.NextShip is not null
                    && player.Ship != playerData.NextShip)
                {
                    // The player respawned after dying and has a different ship set as their next one.
                    // Change the player to that ship.
                    _game.SetShip(player, playerData.NextShip.Value); // this will trigger another Spawn callback
                }
            }

            // Ensure ships are burned on spawn if in a match and match type is configured to burn items.
            if (slot.MatchData.Configuration.BurnItemsOnSpawn)
            {
                RemoveAllItems(slot);
            }
        }

        #endregion

        #region Administrative Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<match type>",
            Description = """
                Loads (or reloads) a match type from configuration.
                Ongoing matches continue to run with their old configuration.
                New matches will use the new configuration.
                """)]
        private void Command_loadmatchtype(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            LoadMatchTypeAsync(parameters.ToString(), player.Name!);

            // async local function since the command handler can't be made async
            async void LoadMatchTypeAsync(string matchType, string playerName)
            {
                ConfigHandle? ch = await _configManager.OpenConfigFileAsync(null, ConfigurationFileName).ConfigureAwait(true); // resume on the mainloop thread

                Player? player = _playerData.FindPlayer(playerName);
                if (player is null)
                    return;

                if (ch is null)
                {
                    _chat.SendMessage(player, $"Error opening configuration file '{ConfigurationFileName}'.");
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Error opening configuration file '{ConfigurationFileName}'.");
                    return;
                }

                try
                {
                    MatchConfiguration? matchConfiguration = CreateMatchConfiguration(ch, matchType);
                    if (matchConfiguration is null)
                    {
                        _chat.SendMessage(player, $"Error creating match configuration for match type '{matchType}' from '{ConfigurationFileName}'.");
                        return;
                    }

                    // league
                    if (matchConfiguration.LeagueId is not null && matchConfiguration.GameTypeId is not null)
                    {
                        if (!_leagueMatchConfigurations.TryAdd(matchConfiguration.GameTypeId.Value, matchConfiguration))
                        {
                            _chat.SendMessage(player, $"Unable to register game type {matchConfiguration.GameTypeId.Value} as a league match configuration for match type {matchConfiguration.MatchType}.");
                        }
                        else
                        {
                            if (_leagueManager is null)
                            {
                                _chat.SendMessage(player, $"No {nameof(ILeagueManager)} to register the {nameof(TeamVersusMatch)} module with for game type {matchConfiguration.GameTypeId.Value}.");
                            }
                            else
                            {
                                if (!_leagueManager.Register(matchConfiguration.GameTypeId.Value, this))
                                {
                                    _chat.SendMessage(player, $"Failed to register the {nameof(TeamVersusMatch)} module with for game type {matchConfiguration.GameTypeId.Value} on the {nameof(ILeagueManager)}.");
                                }
                            }
                        }
                    }

                    string? queueName = matchConfiguration.QueueName;
                    if (queueName is not null)
                    {
                        if (!_queueDictionary.TryGetValue(queueName, out TeamVersusMatchmakingQueue? queue))
                        {
                            queue = CreateQueue(ch, queueName);
                            if (queue is null)
                            {
                                _chat.SendMessage(player, $"Error creating queue '{queueName}' for match type '{matchType}' from '{ConfigurationFileName}'.");
                                return;
                            }

                            if (!_matchmakingQueues.RegisterQueue(queue))
                            {
                                _chat.SendMessage(player, $"Error registering queue '{queueName}' for match type '{matchType}' from '{ConfigurationFileName}'.");
                                return;
                            }

                            _queueDictionary.Add(queueName, queue);
                        }

                        if (!_queueMatchConfigurations.TryGetValue(queueName, out List<MatchConfiguration>? matchConfigurationList))
                        {
                            matchConfigurationList = new List<MatchConfiguration>(1);
                            _queueMatchConfigurations.Add(queueName, matchConfigurationList);
                        }

                        int index = matchConfigurationList.FindIndex(c => c.MatchType == matchType);
                        if (index != -1)
                        {
                            matchConfigurationList.RemoveAt(index);
                        }
                        matchConfigurationList.Add(matchConfiguration);
                    }

                    _matchConfigurationDictionary[matchType] = matchConfiguration;

                    // Remove existing matches.
                    // Only remove inactive ones.
                    // Ongoing matches will continue to run with their old configuration, but will be discarded when they complete.

                    List<MatchIdentifier> toRemove = [];
                    foreach ((MatchIdentifier matchIdentifier, MatchData matchData) in _matchDataDictionary)
                    {
                        if (string.Equals(matchIdentifier.MatchType, matchType, StringComparison.OrdinalIgnoreCase)
                            && matchData.Status == MatchStatus.None)
                        {
                            toRemove.Add(matchIdentifier);
                        }
                    }

                    foreach (MatchIdentifier matchIdentifier in toRemove)
                    {
                        _matchDataDictionary.Remove(matchIdentifier);
                    }

                    _chat.SendMessage(player, $"Loaded match type '{matchType}' from '{ConfigurationFileName}'.");
                }
                finally
                {
                    _configManager.CloseConfigFile(ch);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<match type>",
            Description = """
                Removes a match type, preventing new matches of the type from starting up.
                Ongoing matches continue to run with their existing configuration.
                """)]
        private void Command_unloadmatchtype(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string matchType = parameters.ToString();

            // Remove the match type.
            if (!_matchConfigurationDictionary.Remove(matchType, out MatchConfiguration? matchConfiguration))
            {
                _chat.SendMessage(player, $"Match type '{matchType}' not found.");
                return;
            }

            if (matchConfiguration.LeagueId is not null && _leagueManager is not null && matchConfiguration.GameTypeId is not null)
            {
                if (!_leagueMatchConfigurations.Remove(matchConfiguration.GameTypeId.Value))
                {
                    _chat.SendMessage(player, $"Failed to remove game type {matchConfiguration.GameTypeId.Value} from the known league match configurations.");
                }

                if (!_leagueManager.Unregister(matchConfiguration.GameTypeId.Value, this))
                {
                    _chat.SendMessage(player, $"Failed to unregister game type {matchConfiguration.GameTypeId.Value} from the league manager.");
                }
            }

            string? queueName = matchConfiguration.QueueName;

            if (queueName is not null && _queueMatchConfigurations.TryGetValue(queueName, out List<MatchConfiguration>? matchConfigurationList))
            {
                matchConfigurationList.Remove(matchConfiguration);

                if (matchConfigurationList.Count == 0)
                {
                    // This was the last match type for the queue.
                    _queueMatchConfigurations.Remove(queueName);

                    // Remove the queue.
                    if (_queueDictionary.Remove(queueName, out TeamVersusMatchmakingQueue? queue))
                    {
                        if (_matchmakingQueues.UnregisterQueue(queue))
                        {
                            _chat.SendMessage(player, $"Removed queue '{queueName}' that was used by match type '{matchType}'.");
                        }
                    }
                }
            }

            // Remove existing matches.
            // Only remove inactive ones.
            // Ongoing matches will continue to run with their old configuration, but will be discarded when they complete.

            List<MatchIdentifier> toRemove = [];
            foreach ((MatchIdentifier matchIdentifier, MatchData matchData) in _matchDataDictionary)
            {
                if (string.Equals(matchIdentifier.MatchType, matchType, StringComparison.OrdinalIgnoreCase)
                    && matchData.Status == MatchStatus.None)
                {
                    toRemove.Add(matchIdentifier);
                }
            }

            foreach (MatchIdentifier matchIdentifier in toRemove)
            {
                _matchDataDictionary.Remove(matchIdentifier);
            }

            _chat.SendMessage(player, $"Removed match type '{matchType}'.");
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description = "For a player to request to be subbed out of their current match.")]
        private void Command_requestsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                return;

            PlayerSlot? slot = targetPlayerData.AssignedSlot;
            if (slot is null)
                return;

            if (targetPlayer != player)
            {
                // Check that the player is a captain and the targetPlayer is on their team.
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                Team? team = playerData.CaptainOfTeam;
                if (team is null)
                {
                    _chat.SendMessage(player, "You are not a captain.");
                    return;
                }

                if (slot.Team != team)
                {
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not on your team.");
                    return;
                }
            }

            if (slot.Lives <= 0)
            {
                // The slot has already been knocked out.
                if (targetPlayer == player)
                    _chat.SendMessage(player, "Your assigned slot is not eligible for a sub since it's knocked out.");
                else
                    _chat.SendMessage(player, $"The slot assigned to {targetPlayer.Name} is not eligible for a sub since it's knocked out.");

                return;
            }

            if (slot.AvailableSubSlotNode.List is not null || slot.IsSubRequested)
            {
                // The slot is already open for a sub.
                if (targetPlayer == player)
                    _chat.SendMessage(player, "Your assigned slot is already open for a sub.");
                else
                    _chat.SendMessage(player, $"The slot assigned to {targetPlayer.Name} is already open for sub.");

                return;
            }

            slot.IsSubRequested = true;
            _availableSubSlots.AddLast(slot.AvailableSubSlotNode);

            if (targetPlayer == player)
                _chat.SendArenaMessage(player.Arena, $"{player.Name} has requested to be subbed out.");
            else
                _chat.SendArenaMessage(player.Arena, $"{targetPlayer.Name}'s slot is open to be subbed out.");

            SendSubAvailabilityNotificationToQueuedPlayers(slot.MatchData);
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[<queue name>] | [<slot #>] | [<player name>]",
            Description = $"""
                Allows subbing into a slot in existing match. It works differently for matchmaking vs league.
                When subbing in, the switch may not happen immediately. First, you may need to be moved to the proper arena.
                Next, the slot may currently have an active player (that requested to be subbed out), in which case the 
                active player needs to get to full energy to become eligible to be switched out. If the player is purposely
                not allowing their ship to reach full energy, use ?cancelsub to cancel out.

                Matchmaking
                -----------
                Args: [<queue name>] 
                Searches for the next available slot in any ongoing matches on the queues that you are waiting on (with ?next).
                It will search all queues that you are currently waiting for a match on. You can optionally specify a 
                <queue name>, in which case it will only search for open slots in matches for that queue.

                League
                ------
                Args: [<slot #>] | [<player name>]
                To use this command, the captain of your team first needs to ?{CommandNames.AllowSub} you.
                When used without a target specified (i.e. ?sub), it will attempt to find an eligible slot by searching for:
                an unused slot, a used but currently unassigned slot, or a slot that can be subbed into; in that order.
                The slot to take can be specified by player or slot #.
                To sub in for (replace) another player, send the command as a private message:
                  /?{CommandNames.Sub}  or :<player>:?{CommandNames.Sub}
                Or specify the name of the player the slot is assigned to:
                  ?{CommandNames.Sub} <player name>
                Or specify the slot #:
                  ?{CommandNames.Sub} <slot #>
                """)]
        private void Command_sub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, $"You must be in spec to use ?{CommandNames.Sub}.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.AssignedSlot is not null)
            {
                _chat.SendMessage(player, "You are already playing in a match.");
                return;
            }

            if (playerData.SubSlot is not null)
            {
                _chat.SendMessage(player, $"You already have a ?{CommandNames.Sub} attempt in progress.");
                return;
            }

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? leagueMatch = arenaData.LeagueMatch;
            if (leagueMatch is not null)
            {
                // Each player can only participate once in the match.
                foreach (PlayerParticipationRecord record in leagueMatch.ParticipationList)
                {
                    if (string.Equals(record.PlayerName, player.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _chat.SendMessage(player, "You have already particpated in the match.");
                        return;
                    }
                }

                // Get the player's team
                Team? team = null;
                foreach (Team otherTeam in leagueMatch.Teams)
                {
                    if (otherTeam.Freq == player.Freq)
                    {
                        team = otherTeam;
                        break;
                    }
                }

                if (team is null)
                    return;

                if (leagueMatch.Status == MatchStatus.Initializing || leagueMatch.Status == MatchStatus.StartingCountdown)
                {
                    // Special rules apply before the match starts since slots are not assigned until it actually starts.
                    if (!CanPlayAsStarterInLeagueMatch(arena, player, team))
                        return;

                    // Slots are not assigned until the game starts, so just put the player in a ship.
                    _game.SetShip(player, playerData.NextShip ?? ShipType.Warbird);
                    return;
                }

                if (leagueMatch.Status != MatchStatus.InProgress)
                    return;

                // TODO: add a check to make sure the player hasn't already played in the match
                //leagueMatch.ParticipationList

                if (!team.AllowedToSub.Contains(player.Name!))
                {
                    _chat.SendMessage(player, "Your captain must first allow you to sub using ?allowsub.");
                    return;
                }

                PlayerSlot? slot = null;
                if (target.TryGetPlayerTarget(out Player? targetPlayer))
                {
                    // Attempting to sub-in for a specific player.
                    if (targetPlayer.Arena != arena)
                        return;

                    if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                        return;

                    PlayerSlot? targetSlot = targetPlayerData.AssignedSlot;
                    if (targetSlot is null)
                    {
                        _chat.SendMessage(player, $"{targetPlayer.Name} is not assigned a slot.");
                        return;
                    }

                    if (targetSlot.Team != team)
                    {
                        _chat.SendMessage(player, $"{targetPlayer.Name} is not on your team.");
                        return;
                    }

                    slot = targetSlot;
                }
                else if (!parameters.IsWhiteSpace())
                {
                    // Try by slot #.
                    if (int.TryParse(parameters, out int slotIdx))
                    {
                        if (slotIdx < 0 || slotIdx >= team.Slots.Length)
                        {
                            _chat.SendMessage(player, $"Invalid slot # specified. (min: 0, max: {team.Slots.Length - 1}");
                            return;
                        }

                        slot = team.Slots[slotIdx];
                    }
                    else
                    {
                        // Try by player name.
                        foreach (PlayerSlot otherSlot in team.Slots)
                        {
                            if (parameters.Equals(otherSlot.PlayerName, StringComparison.OrdinalIgnoreCase))
                            {
                                slot = otherSlot;
                                break;
                            }
                        }

                        if (slot is null)
                        {
                            _chat.SendMessage(player, $"A slot assigned to '{parameters}' could not be found.");
                            return;
                        }
                    }
                }

                if (slot is null)
                {
                    // No target slot was specified (either by private message, slot #, or player name).

                    // Look for an unused (never assigned) slot that can be filled.
                    foreach (PlayerSlot otherSlot in team.Slots)
                    {
                        if (otherSlot.Status == PlayerSlotStatus.Waiting && string.IsNullOrWhiteSpace(otherSlot.PlayerName))
                        {
                            slot = otherSlot;
                            break;
                        }
                    }

                    if (slot is null)
                    {
                        // Otherwise, look for a used, but currently unassigned slot to sub into.
                        foreach (PlayerSlot otherSlot in team.Slots)
                        {
                            if (otherSlot.Status == PlayerSlotStatus.Waiting)
                            {
                                slot = otherSlot;
                                break;
                            }
                        }

                        if (slot is null)
                        {
                            // Otherwise, look for a slot that can be subbed into.
                            foreach (PlayerSlot otherSlot in team.Slots)
                            {
                                if (otherSlot.CanBeSubbed && otherSlot.SubPlayer is null)
                                {
                                    slot = otherSlot;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (slot is null)
                {
                    _chat.SendMessage(player, "Eligible slot not found.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(slot.PlayerName))
                {
                    // Fill the unused (never assigned) slot.
                    AssignSlot(slot, player);
                    slot.Status = PlayerSlotStatus.Playing;

                    // Put the player in with a full ship of their choice.
                    SetShipAndFreq(slot, false, null, ItemsAction.Full);

                    // Allow the player to ship change after subbing in for a limited amount of time.
                    TimeSpan allowShipChangeDuration = slot.MatchData.Configuration.AllowShipChangeAfterSubDuration;
                    slot.AllowShipChangeExpiration = allowShipChangeDuration > TimeSpan.Zero
                        ? DateTime.UtcNow + allowShipChangeDuration
                        : null;
                    slot.AllowShipChangeReason = slot.AllowShipChangeExpiration is not null ? AllowShipChangeReason.FillUnused : null;
                    slot.ShipChangeItemsAction = ItemsAction.Full;
                    slot.ShipChangeCount = 0;

                    // Send a notification.
                    HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        GetPlayersToNotify(slot.MatchData, players);
                        players.Add(player);

                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            sb.Append($"{player.Name} in (unused slot) on Freq {slot.Team.Freq} -- {slot.Lives} {(slot.Lives == 1 ? "life" : "lives")}");

                            TimeSpan gameTime = leagueMatch.Started is not null ? DateTime.UtcNow - leagueMatch.Started.Value : TimeSpan.Zero;

                            sb.Append(" [");
                            sb.AppendFriendlyTimeSpan(gameTime);
                            sb.Append(']');

                            _chat.SendSetMessage(players, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(players);
                    }

                    TeamVersusMatchPlayerSubbedCallback.Fire(arena, slot, null);
                }
                else
                {
                    // The slot is already assigned, try to sub into it.
                    if (!slot.CanBeSubbed)
                    {
                        if (slot.Status == PlayerSlotStatus.KnockedOut)
                            _chat.SendMessage(player, "The slot is knocked out.");
                        else
                            _chat.SendMessage(player, "The slot cannot be subbed (requires ?requestsub or the existing player to spec).");

                        return;
                    }

                    if (slot.SubPlayer is not null)
                    {
                        _chat.SendMessage(player, $"{slot.SubPlayer.Name} is already attempting to sub into the slot.");
                        return;
                    }

                    playerData.SubSlot = slot; // Keep track of the slot the player is trying to sub into.
                    slot.SubPlayer = player; // So that other players can't try to sub into the slot.

                    SubSlot(slot);
                    return;
                }
            }
            else
            {
                // TODO: Maybe allow players that are not in the matchmaking queue to sub in?

                // Find the next available slot, if one exists.
                PlayerSlot? subSlot = null;

                foreach (PlayerSlot slot in _availableSubSlots)
                {
                    if (slot.CanBeSubbed
                        && slot.SubPlayer is null // no sub in progress
                        && (parameters.IsWhiteSpace() || parameters.Equals(slot.MatchData.Configuration.QueueName, StringComparison.OrdinalIgnoreCase))
                        && slot.MatchData.Configuration.QueueName is not null
                        && _queueDictionary.TryGetValue(slot.MatchData.Configuration.QueueName, out TeamVersusMatchmakingQueue? queue)
                        && queue.ContainsSoloPlayer(player))
                    {
                        subSlot = slot;
                        break;
                    }
                }

                if (subSlot is null)
                {
                    _chat.SendMessage(player, "There are no available slots to sub into at this moment.");
                    return;
                }

                MatchData matchData = subSlot.MatchData;
                arena = _arenaManager.FindArena(matchData.ArenaName);
                if (arena is null)
                    return;

                // Set the 'playing' status now. Otherwise, the player could get pulled into a match while in the process of subbing in.
                _matchmakingQueues.SetPlayingAsSub(player);

                playerData.SubSlot = subSlot; // Keep track of the slot the player is trying to sub into.
                subSlot.SubPlayer = player; // So that other players can't try to sub into the slot.

                if (player.Arena != arena)
                {
                    _chat.SendMessage(player, $"You are being moved to arena {arena.Name} to become a substitute player in an ongoing match. Please stand by.");

                    _arenaManager.SendToArena(player, arena.Name, 0, 0);

                    // When the player enters the arena fully, it will check playerData.SubSlot and continue from there.
                    // If the player never fully enters the arena (e.g. disconnects), it will do the same and the sub will be cancelled.
                    return;
                }
                else
                {
                    SubSlot(subSlot);
                }
            }
        }

        private bool CanPlayAsStarterInLeagueMatch(Arena arena, Player player, Team team)
        {
            if (team.NonStarter.Contains(player.Name!))
            {
                _chat.SendMessage(player, "You are not allowed to play as a starter (captain controls this using ?allowplay).");
                return false;
            }

            // Check that there is room for another to play on the freq.
            int teamPlaying = 0;
            foreach (Player otherPlayer in _playerData.Players)
            {
                if (otherPlayer.Arena == arena
                    && otherPlayer.Freq == team.Freq
                    && otherPlayer.Ship != ShipType.Spec)
                {
                    teamPlaying++;
                }
            }

            if (teamPlaying >= team.MatchData.Configuration.PlayersPerTeam)
            {
                _chat.SendMessage(player, "Your team already at the maximum allowed playing in ships.");
                return false;
            }

            // Slots are not assigned until the game starts, so just put the player in a ship.
            return true;
        }

        private void SubSlot(PlayerSlot slot)
        {
            if (!slot.CanBeSubbed)
            {
                CancelSubInProgress(slot, true);
                return;
            }

            Player? player = slot.SubPlayer;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
            {
                // No ongoing sub for the slot.
                return;
            }

            MatchData matchData = slot.MatchData;
            Arena? arena = player.Arena;
            if (arena is null
                || !string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase)
                || !playerData.HasFullyEnteredArena)
            {
                // The player trying to sub in is not yet in the arena.
                return;
            }

            if (slot.Status == PlayerSlotStatus.Playing
                && slot.Player is not null
                && slot.Player.Ship != ShipType.Spec
                && !slot.IsFullEnergy)
            {
                // The currently assigned player is still playing and isn't at full energy.
                // Wait until the player gets to full energy.

                // This method will be called again by:
                // - the position packet handler (when the currently assigned player's energy gets to max)
                // - the player action handler (when the currently assigned player leaves the arena)
                // - the ship change handler (when the currently assigned player switches to spec)

                _chat.SendMessage(slot.Player, $"A player is waiting to sub in for your slot. Get to full energy to be subbed out automatically.");
                _chat.SendMessage(player, $"You will be subbed in when {slot.Player.Name} gets to full energy. Please stand by, or use ?{CommandNames.CancelSub} to cancel.");
                return;
            }

            if (slot.Status == PlayerSlotStatus.Playing
                && slot.Player is not null
                && slot.Player.Arena == arena
                && slot.Player.Ship != ShipType.Spec)
            {
                // First, move the old player to spectator mode.
                _game.SetShip(slot.Player, ShipType.Spec);
            }
            else
            {
                // Adjust the participation record for the player being subbed out.

                // Find the record for the player being subbed out.
                int subOutIndex = -1;
                for (int i = 0; i < matchData.ParticipationList.Count; i++)
                {
                    if (string.Equals(matchData.ParticipationList[i].PlayerName, slot.PlayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        subOutIndex = i;
                        break;
                    }
                }

                if (subOutIndex != -1)
                {
                    PlayerParticipationRecord subOutRecord = matchData.ParticipationList[subOutIndex];
                    if (subOutRecord.LeftWithoutSub)
                    {
                        // The player being subbed out left before a sub was available.
                        // Move the sub-out player's record to the end of the list, but before any with LeftWithoutSub = true.
                        matchData.ParticipationList.RemoveAt(subOutIndex);

                        for (subOutIndex = matchData.ParticipationList.Count - 1; subOutIndex >= 0; subOutIndex--)
                        {
                            if (!matchData.ParticipationList[subOutIndex].LeftWithoutSub)
                                break;
                        }

                        subOutIndex++;
                        matchData.ParticipationList.Insert(subOutIndex, subOutRecord);
                    }
                }
            }

            // Add a participation record for the player being subbed in.
            int subInIndex;
            for (subInIndex = 0; subInIndex < matchData.ParticipationList.Count; subInIndex++)
            {
                if (!matchData.ParticipationList[subInIndex].WasSubIn)
                    break;
            }
            matchData.ParticipationList.Insert(subInIndex, new PlayerParticipationRecord(player.Name!, true, false));

            // Clear the available/pending sub.
            slot.AvailableSubSlotNode.List?.Remove(slot.AvailableSubSlotNode);
            slot.SubPlayer = null;
            playerData.SubSlot = null;

            // Do the actual sub in.
            string? subOutPlayerName = slot.PlayerName;
            Player? subOutPlayer = slot.Player; // can be null if the player disconnected
            AssignSlot(slot, player);
            slot.Status = PlayerSlotStatus.Playing;

            // When a slot is subbed, it is no longer tied to an original pre-made group.
            // This is done before calling TeamVersusMatchPlayerSubbedCallback because the stats module will read it.
            slot.PremadeGroupId = null;

            // Fire the callback purposely before setting the player's freq and ship as we want this event to occur before the ship change event.
            TeamVersusMatchPlayerSubbedCallback.Fire(arena, slot, subOutPlayerName);

            // TODO: Determine whether the ship must be the same ship that was used by the previous assigned or one chosen by the newly assigned player.
            // TODO: Determine whether to: restored items with what the previous player had, burn items, or given a full ship.

            bool usePriorShip;
            ItemsAction itemsAction = ItemsAction.Full;

            StringBuilder details = _objectPoolManager.StringBuilderPool.Get();
            StringBuilder fineDetails = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                bool isDeathSub = false;
                bool isFullSub = false;

                bool tooLateForDeathSub = false;
                bool noRemainingFullSubs = false;
                bool slotNotMarkedForFullSub = false;

                // DeathSub
                if (matchData.Configuration.DeathSubDuration > TimeSpan.Zero // DeathSubs are enabled
                    && slot.LastDeathForDeathSub is not null)
                {
                    if (DateTime.UtcNow > (slot.LastDeathForDeathSub + matchData.Configuration.DeathSubDuration))
                    {
                        isDeathSub = true;
                        usePriorShip = false;
                        itemsAction = ItemsAction.Full;

                        // Only eligible once per death.
                        slot.LastDeathForDeathSub = null;

                        details.Append("DeathSub -- not burned");
                    }
                    else
                    {
                        tooLateForDeathSub = true;
                    }
                }

                // FullSub (league only)
                if (!isDeathSub && matchData.LeagueGame is not null)
                {
                    if (slot.Team.RemainingFullSubs > 0)
                    {
                        if (slot.FullSubEnabled)
                        {
                            isFullSub = true;
                            usePriorShip = false;
                            itemsAction = ItemsAction.Full;

                            // Use up one of the remaining FullSubs.
                            slot.Team.RemainingFullSubs--;

                            details.Append("FullSub");
                        }
                        else
                        {
                            slotNotMarkedForFullSub = true;
                        }
                    }
                    else
                    {
                        noRemainingFullSubs = true;
                    }
                }

                if (!isDeathSub && !isFullSub)
                {
                    usePriorShip = matchData.Configuration.SubInMustUsePriorShip;
                    itemsAction = matchData.Configuration.SubInItemsAction;

                    switch (itemsAction)
                    {
                        case ItemsAction.Full:
                            break;
                        case ItemsAction.Burn:
                            details.Append("Burned");
                            break;
                        case ItemsAction.Restore:
                            details.Append("Remaining Items Restored");
                            break;
                        default:
                            break;
                    }
                }

                if (itemsAction != ItemsAction.Full)
                {
                    if (tooLateForDeathSub)
                    {
                        fineDetails.AppendCommaDelimited("too late for a DeathSub");
                    }

                    if (noRemainingFullSubs)
                    {
                        fineDetails.AppendCommaDelimited("no FullSubs left");
                    }

                    if (slotNotMarkedForFullSub)
                    {
                        fineDetails.AppendCommaDelimited("slot not marked for FullSub");
                    }
                }

                if (fineDetails.Length > 0)
                {
                    details.Append(" (");
                    details.Append(fineDetails);
                    details.Append(')');
                }

                // Send a notification.
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(slot.MatchData, players);
                    players.Add(player);

                    if (subOutPlayer is not null)
                        players.Add(subOutPlayer);

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        sb.Append($"{player.Name} in for {subOutPlayerName} on Freq {slot.Team.Freq}");

                        if (details.Length > 0)
                        {
                            sb.Append(" [");
                            sb.Append(details);
                            sb.Append(']');
                        }

                        sb.Append($" -- {slot.Lives} {(slot.Lives == 1 ? "life" : "lives")}");

                        TimeSpan gameTime = matchData.Started is not null ? DateTime.UtcNow - matchData.Started.Value : TimeSpan.Zero;

                        sb.Append(" [");
                        sb.AppendFriendlyTimeSpan(gameTime);
                        sb.Append(']');

                        _chat.SendSetMessage(players, sb);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(details);
                _objectPoolManager.StringBuilderPool.Return(fineDetails);
            }

            if (itemsAction == ItemsAction.Restore)
            {
                // Keep a copy of what the items were before the sub-in.
                // This is also used if the player subsequently changes ship within the allowed timeframe (ship change after sub).
                slot.RestoreItems = slot.Items;
            }

            // Allow the player to ship change after sub-in for a limited amount of time.
            TimeSpan allowShipChangeDuration = slot.MatchData.Configuration.AllowShipChangeAfterSubDuration;
            slot.AllowShipChangeExpiration = allowShipChangeDuration > TimeSpan.Zero
                ? DateTime.UtcNow + allowShipChangeDuration
                : null;
            slot.AllowShipChangeReason = slot.AllowShipChangeExpiration is not null ? AllowShipChangeReason.Sub : null;
            slot.ShipChangeItemsAction = itemsAction;
            slot.ShipChangeCount = 0;

            // Finally, get the sub-in player into the game (on the correct freq and in the proper ship).
            SetShipAndFreq(slot, true, null, itemsAction);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = $"""
                Cancels a ?{CommandNames.Sub} attempt that's in progress."
                "Use this if you no longer want to wait to be subbed in.
                """)]
        private void Command_cancelsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.SubSlot is null)
            {
                _chat.SendMessage(player, $"There is no ?{CommandNames.Sub} attempt in progress to cancel.");
                return;
            }

            CancelSubInProgress(playerData.SubSlot, true);
        }

        private void CancelSubInProgress(PlayerSlot slot, bool notify)
        {
            Player? player = slot.SubPlayer;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            playerData.SubSlot = null;
            slot.SubPlayer = null;

            if (notify)
            {
                _chat.SendMessage(player, $"The ?{CommandNames.Sub} attempt was cancelled.");
            }

            // Remove the 'playing' state which requeues the player back into the queues.
            // In this scenario, the player should get added back into their original queue positions rather than at the end of the queues.
            _matchmakingQueues.UnsetPlaying(player, true);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Returns a player to their current match (e.g. after getting spec'd or disconnected).")]
        private void Command_return(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Ship != ShipType.Spec
                || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
            {
                return;
            }

            if (!CanReturnToMatch(player, playerData, true))
            {
                return;
            }

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
            {
                return;
            }

            MatchData matchData = slot.MatchData;
            if (player.Arena is null || !string.Equals(player.Arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
            {
                // Send the player to the proper arena.
                _arenaManager.SendToArena(player, matchData.ArenaName, 0, 0);

                // Mark that the player is returning, so that when the player does enter the arena, it will know to attempt a return to match.
                playerData.IsReturning = true;
                return;
            }
            else
            {
                ReturnToMatch(player, playerData);
            }
        }

        private void ReturnToMatch(Player player, PlayerData playerData)
        {
            if (player is null || playerData is null)
                return;

            if (!CanReturnToMatch(player, playerData, true))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            // Update participation record.
            MatchData matchData = slot.MatchData;
            for (int index = 0; index < matchData.ParticipationList.Count; index++)
            {
                if (string.Equals(matchData.ParticipationList[index].PlayerName, slot.PlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    matchData.ParticipationList[index] = matchData.ParticipationList[index] with { LeftWithoutSub = false };
                    break;
                }
            }

            ItemsAction itemsAction = matchData.Status == MatchStatus.InProgress
                ? matchData.Configuration.ReturnToMatchItemsAction
                : ItemsAction.Full;

            if (itemsAction == ItemsAction.Restore)
            {
                // Save a copy of what the prior items were.
                slot.RestoreItems = slot.Items;
            }

            SetShipAndFreq(slot, true, null, itemsAction);

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetPlayersToNotify(slot.MatchData, players);
                players.Add(player);

                if (slot.SubPlayer is not null)
                {
                    players.Add(slot.SubPlayer);
                }

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    sb.Append($"{player.Name} returned to the match.");
                    
                    string itemsActionDescription = GetItemsActionDescription(slot.ShipChangeItemsAction);
                    if (!string.IsNullOrEmpty(itemsActionDescription))
                        sb.Append($" [{itemsActionDescription}]");

                    sb.Append($" [Lives: {slot.Lives}]");

                    _chat.SendSetMessage(players, sb);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }

            // There is a chance that another player was in the middle of trying to sub in, but didn't complete it yet.
            // For example, the other player used ?sub and is switching arenas, but this player used ?return before
            // the other player entered the arena.
            if (slot.SubPlayer is not null)
            {
                CancelSubInProgress(slot, true);
            }
        }

        private bool CanReturnToMatch(Player player, PlayerData playerData, bool notify)
        {
            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
            {
                if (notify)
                {
                    _chat.SendMessage(player, "You are not assigned to a slot in a match.");
                }

                return false;
            }

            MatchData matchData = slot.MatchData;
            if (matchData.Status != MatchStatus.Initializing && matchData.Status != MatchStatus.InProgress)
            {
                return false;
            }

            if (slot.Status != PlayerSlotStatus.Waiting)
            {
                if (notify)
                {
                    if (slot.Status == PlayerSlotStatus.Playing)
                        _chat.SendMessage(player, "You cannot return to the match because the slot is already filled");
                    else if (slot.Status == PlayerSlotStatus.KnockedOut)
                        _chat.SendMessage(player, "You cannot return to the match because the slot has been knocked out.");
                    else
                        _chat.SendMessage(player, "You cannot return to the match.");
                }

                return false;
            }

            if (slot.LagOuts >= matchData.Configuration.MaxLagOuts)
            {
                if (notify)
                {
                    _chat.SendMessage(player, $"You cannot return to the match because you have exceeded the maximum # of LagOuts: {matchData.Configuration.MaxLagOuts}.");
                }

                return false;
            }

            return true;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Request that the game be restarted, needs a majority vote")]
        private void Command_restart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requested to restart the game. Vote using: ?restart [y|n]");
            //_chat.SendMessage(, $"{player.Name} [agreed with|vetoed] the restart request. (For:{forVotes}, Against:{againstVotes})");
            //_chat.SendMessage(, $"The vote to ?restart has expired.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = """
                Request that teams be randomized and the game restarted, needs a majority vote.
                Using this ignores player groups, all players in the match will be randomized.
                """)]
        private void Command_randomize(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requests to re-randomize the teams and restart the game. To agree, type: ?randomize");
            //_chat.SendMessage(, $"{player.Name} agreed to ?randomize. ({forVotes}/5)");
            //_chat.SendMessage(, $"The vote to ?randomize has expired.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = """
                Request that the game be ended as a loss for the team,
                if all remaining players on a team agree (type the command), 
                the game will end as a loss to that team without having to be killed out.
                """)]
        private void Command_end(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // To the opposing team (voting begin)
            //_chat.SendMessage(, $"{player.Name} has requested their team to surrender. Their team is now voting on it.");

            // To the opposing team (vote passed or no vote needed - last player)
            //_chat.SendMessage(, $"Your opponents have surrendered.");

            // To the opposing team (vote failed)
            //_chat.SendMessage(, $"Your opponents chose not to surrender. Show no mercy!");

            // To the player's team (voting begin)
            //_chat.SendMessage(, $"Your teammate, {player.Name}, has requested to surrender with honor. Vote using: ?end [y|n]");

            // To the player's team (vote passed or no vote needed - last player)
            //_chat.SendMessage(, $"Your team has surrendered.");

            // To the player's team (vote failed)
            //_chat.SendMessage(, $"Your team chose to keep playing.");

            // To the player's team (vote expired)
            //_chat.SendMessage(, $"The vote to surrender has expired. Fight on!");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Request that the game be ended as a draw by agreement (no winner), needs a majority vote across all remaining players.")]
        private void Command_draw(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<ship #>",
            Description = """
                Requests a ship change (which ship to use next in a match).
                When in a match, it will immediately change a player's current ship 
                if allowed (during match startup or the short period after death,
                and the player is at full energy).
                """)]
        private void Command_sc(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (!TryParseShip(player, parameters, out ShipType ship))
            {
                _chat.SendMessage(player, "Invalid ship type specified.");
                return;
            }

            PlayerSlot? playerSlot = playerData.AssignedSlot;
            if (playerSlot is not null // is in a match
                && CanShipChangeNow(player, playerData)
                // Additionally make sure the player is at full energy (don't want this command to have abilities above that of a regular ship change)
                && player.Arena is not null
                && _arenaDataDictionary.TryGetValue(player.Arena, out ArenaData? arenaData)
                && player.Position.Energy >= arenaData.ShipSettings[(int)player.Ship].MaximumEnergy)
            {
                // Set the player's next ship and change the ship now.
                SetNextShip(player, playerData, ship, false);
                SetShipAndFreq(playerSlot, false, null, playerSlot.ShipChangeItemsAction);
            }
            else
            {
                // Just set the next ship.
                SetNextShip(player, playerData, ship, true);
            }

            // local function that parses input for ship selection
            static bool TryParseShip(Player player, ReadOnlySpan<char> s, out ShipType ship)
            {
                if (!int.TryParse(s, out int shipNumber) || shipNumber < 1 || shipNumber > 8)
                {
                    ship = ShipType.Spec;
                    return false;
                }

                ship = (ShipType)(shipNumber - 1);
                return true;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = """
                Prints out a list of items that remaining players have.
                Only when enabled for the match type.
                """)]
        private void Command_items(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_matchFocus.GetFocusedMatch(player) is not MatchData matchData)
                return;

            if (matchData.Status != MatchStatus.InProgress)
            {
                _chat.SendMessage(player, "Match has not started.");
                return;
            }

            switch (matchData.Configuration.ItemsCommandOption)
            {
                case ItemsCommandOption.RepelsAndRockets:
                    StringBuilder teamBuilder = _objectPoolManager.StringBuilderPool.Get();
                    StringBuilder slotsBuilder = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        foreach (Team team in matchData.Teams)
                        {
                            slotsBuilder.Clear();
                            foreach (PlayerSlot slot in team.Slots)
                            {
                                if (slot.Lives <= 0)
                                    continue;

                                if (slotsBuilder.Length > 0)
                                    slotsBuilder.Append(", ");

                                slotsBuilder.Append($"{slot.PlayerName} {slot.Items.Repels}/{slot.Items.Rockets}");
                            }

                            if (slotsBuilder.Length <= 0)
                                continue;

                            teamBuilder.Clear();
                            teamBuilder.Append($"Freq {team.Freq}: ");
                            teamBuilder.Append(slotsBuilder);

                            _chat.SendMessage(player, teamBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(teamBuilder);
                        _objectPoolManager.StringBuilderPool.Return(slotsBuilder);
                    }
                    break;

                case ItemsCommandOption.None:
                default:
                    break;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = "Displays information about the current match.")]
        private void Command_matchinfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (arenaData.LeagueMatch is not null)
            {
                PrintMatchInfo(player, arenaData.LeagueMatch);
            }
            else
            {
                IMatch? focusedMatch = _matchFocus.GetFocusedMatch(player);
                if (focusedMatch is null || focusedMatch is not MatchData matchData)
                {
                    _chat.SendMessage(player, "No match found.");
                    return;
                }

                PrintMatchInfo(player, matchData);
            }

            void PrintMatchInfo(Player player, MatchData matchData)
            {
                if (matchData.LeagueGame is not null)
                {
                    LeagueGameInfo leagueGame = matchData.LeagueGame;

                    _chat.SendMessage(player, "League Match");
                    _chat.SendMessage(player, $"ID: {leagueGame.SeasonGameId}");
                    _chat.SendMessage(player, $"League: {leagueGame.LeagueName}");
                    _chat.SendMessage(player, $"Season: {leagueGame.SeasonName}");

                    if (leagueGame.RoundNumber is not null)
                        _chat.SendMessage(player, $"Round: {leagueGame.RoundNumber}");
                }
                else
                {
                    _chat.SendMessage(player, "Matchmaking Match");
                    _chat.SendMessage(player, $"Match Type: {matchData.Configuration.MatchType}");

                    if(!string.IsNullOrWhiteSpace(matchData.Configuration.QueueName))
                        _chat.SendMessage(player, $"Queue: {matchData.Configuration.QueueName}");
                }

                foreach (Team team in matchData.Teams)
                {
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        sb.Append($"Freq {team.Freq}");
                        if (team.LeagueTeam is not null)
                        {
                            sb.Append($": {team.LeagueTeam.TeamName}");
                        }

                        _chat.SendMessage(player, sb);
                        sb.Clear();

                        if (team.LeagueTeam is not null)
                        {
                            _chat.SendMessage(player, $"  CAP: {team.Captain?.Name ?? "<none>"}  --  Fullsubs remaining: {team.RemainingFullSubs}");
                        }

                        foreach (PlayerSlot slot in team.Slots)
                        {
                            // For a league match that has not yet started, the slots will not yet be assigned.
                            if (slot.Status == PlayerSlotStatus.None)
                                continue;

                            sb.Append($"{slot.SlotIdx,5}: ");

                            if (string.IsNullOrWhiteSpace(slot.PlayerName))
                                sb.Append("[unused]");
                            else
                                sb.Append(slot.PlayerName);

                            switch (slot.Status)
                            {
                                case PlayerSlotStatus.Waiting:
                                    sb.Append(" [Lag Out]");
                                    break;
                                case PlayerSlotStatus.KnockedOut:
                                    sb.Append(" [Knocked Out]");
                                    break;
                                default:
                                    break;
                            }

                            _chat.SendMessage(player, sb);
                            sb.Clear();
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                Displays rosters of the teams in the current league match.
                Only includes players that can participate in this match.
                In other words, suspended players are excluded.
                Use ?roster <team name> for the full roster.
                """)]
        private void Command_rosters(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? leagueMatch = arenaData.LeagueMatch;
            if (leagueMatch is null)
            {
                _chat.SendMessage(player, "There currently is no league match in the arena.");
                return;
            }

            // Here's what the output should look like with an odd # of teams.
            //
            // +=============================================================+
            // | F 1000: Team1                | F 2000: Team2                |
            // +------------------------------+------------------------------+
            // | [C] Name1                    | [C] Name1                    |
            // |     Name2                    |     Name2                    |
            // |                              |     Name3                    |
            // ===============================================================
            // | F 3000: Team3                |
            // +------------------------------+
            // | [C] Name1                    |
            // |     Name2                    |
            // ================================

            _chat.SendMessage(player, "===============================================================");

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                for (int teamIdx = 0; teamIdx < leagueMatch.Teams.Length;)
                {
                    Team team1 = leagueMatch.Teams[teamIdx++];
                    Team? team2 = teamIdx < leagueMatch.Teams.Length ? leagueMatch.Teams[teamIdx++] : null;

                    LeagueTeamInfo? leagueTeam1 = team1.LeagueTeam;
                    LeagueTeamInfo? leagueTeam2 = team2?.LeagueTeam;

                    if (leagueTeam1 is null)
                        break;

                    // Freq and Team Name
                    sb.Append($"| F {team1.Freq:D4}: {leagueTeam1.TeamName,-20} |");
                    if (team2 is not null && leagueTeam2 is not null)
                    {
                        sb.Append($" F {team2.Freq:D4}: {leagueTeam2.TeamName,-20} |");
                    }

                    _chat.SendMessage(player, sb);
                    sb.Clear();

                    sb.Append("+------------------------------+");
                    if (team2 is not null)
                    {
                        sb.Append("------------------------------+");
                    }

                    _chat.SendMessage(player, sb);
                    sb.Clear();

                    // Roster
                    List<RosterItem> roster1 = new(leagueTeam1.Roster.Count);
                    List<RosterItem>? roster2 = leagueTeam2 is not null ? new(leagueTeam2.Roster.Count) : null;
                    // TODO: Use an object pool for List<RosterItem>

                    foreach ((string playerName, bool isCaptain) in leagueTeam1.Roster)
                    {
                        roster1.Add(new RosterItem(playerName, isCaptain));
                    }

                    if (roster2 is not null && leagueTeam2 is not null)
                    {
                        foreach ((string playerName, bool isCaptain) in leagueTeam2.Roster)
                        {
                            roster2.Add(new RosterItem(playerName, isCaptain));
                        }
                    }

                    roster1.Sort(RosterItemComparer);
                    roster2?.Sort(RosterItemComparer);

                    int end = roster2 is null ? roster1.Count : int.Max(roster1.Count, roster2.Count);
                    for (int rosterIdx = 0; rosterIdx < end; rosterIdx++)
                    {
                        if (rosterIdx < roster1.Count)
                        {
                            RosterItem item = roster1[rosterIdx];
                            sb.Append($"| {(item.IsCaptain ? "[C]" : "   ")} {item.PlayerName,-20}     |");
                        }
                        else
                        {
                            sb.Append("|                              |");
                        }

                        if (roster2 is not null)
                        {
                            if (rosterIdx < roster2.Count)
                            {
                                RosterItem item = roster2[rosterIdx];
                                sb.Append($" {(item.IsCaptain ? "[C]" : "   ")} {item.PlayerName,-20}     |");
                            }
                            else
                            {
                                sb.Append("                              |");
                            }

                            _chat.SendMessage(player, sb);
                            sb.Clear();
                        }
                    }

                    // Footer
                    sb.Append("================================");
                    if (team2 is not null)
                    {
                        sb.Append("===============================");
                    }

                    _chat.SendMessage(player, sb);
                    sb.Clear();
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private static int RosterItemComparer(RosterItem x, RosterItem y)
        {
            // Captains first
            if (x.IsCaptain)
            {
                if (!y.IsCaptain)
                    return -1;
            }
            else if (y.IsCaptain)
            {
                return 1;
            }

            // Then by player name
            return StringComparer.OrdinalIgnoreCase.Compare(x.PlayerName, y.PlayerName);
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "",
            Description = """
                Changes who the current captain of the freq is. (League games only)
                If there is no captain, anyone on the team roster can take cap.
                An official captain of a team can take cap from an unofficial captain.
                This command can be sent privately to another player to transfer captain powers to the target player.
                """)]
        private void Command_cap(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? matchData = arenaData.LeagueMatch;
            if (matchData is null)
                return;

            Team? team;
            LeagueTeamInfo? leagueTeam;

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                team = playerData.CaptainOfTeam;
                if (team is null)
                {
                    _chat.SendMessage(player, "You are not a captain.");
                    return;
                }

                leagueTeam = team.LeagueTeam;
                if (leagueTeam is null)
                    return;

                if (!leagueTeam.Roster.ContainsKey(targetPlayer.Name!))
                {
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not on the roster.");
                    return;
                }

                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                    return;

                if (targetPlayerData.CaptainOfTeam is not null)
                    return;

                // Transfer cap
                playerData.CaptainOfTeam = null;
                team.Captain = targetPlayer;
                targetPlayerData.CaptainOfTeam = team;
                
                AnnounceCaptainChange(arena, team, leagueTeam);
                return;
            }

            team = null;
            foreach (Team otherTeam in matchData.Teams)
            {
                if (otherTeam.Freq == player.Freq)
                {
                    team = otherTeam;
                    break;
                }
            }

            if (team is null)
                return;

            leagueTeam = team.LeagueTeam;
            if (leagueTeam is null)
                return;

            if (team.Captain is null)
            {
                // Give captain to the player.
                team.Captain = player;
                playerData.CaptainOfTeam = team;

                AnnounceCaptainChange(arena, team, leagueTeam);
                return;
            }

            if (team.Captain == player)
            {
                _chat.SendMessage(player, $"You're already captain of freq {team.Freq}.");
                return;
            }

            // Another player is already assigned captain of the freq. Check if we can override it.
            if (!leagueTeam.Roster.TryGetValue(player.Name!, out bool isOfficialCap) || !isOfficialCap)
            {
                _chat.SendMessage(player, $"You cannot take captain of the freq since you are not an official captain of team: {leagueTeam.TeamName}");
                return;
            }

            if (leagueTeam.Roster.TryGetValue(team.Captain.Name!, out bool currentIsOfficialCap) && currentIsOfficialCap)
            {
                _chat.SendMessage(player, $"You cannot take captain from official captain {team.Captain.Name!}.");
                return;
            }

            // Override captain
            if (!team.Captain.TryGetExtraData(_pdKey, out PlayerData? oldPlayerData))
                return;

            oldPlayerData.CaptainOfTeam = null;
            team.Captain = player;
            playerData.CaptainOfTeam = team;

            AnnounceCaptainChange(arena, team, leagueTeam);

            // local helper for notifying the arena of a change in who is captain
            void AnnounceCaptainChange(Arena arena, Team team, LeagueTeamInfo leagueTeam)
            {
                if (team.Captain is null)
                    return;

                _chat.SendArenaMessage(arena, $"{team.Captain.Name} is now the captain for freq {team.Freq} ({leagueTeam.TeamName}).");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                For captains to indicate that their team is ready.
                League games only.
                The match will begin when all captains have indicated that their teams are ready,
                or a referee forces the match to begin.
                """)]
        private void Command_ready(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? matchData = arenaData.LeagueMatch;
            if (matchData is null)
                return;

            if (matchData.IsForcedStart)
            {
                // The ?ready command is disabled when a match is forced to start.
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Team? team = playerData.CaptainOfTeam;
            if (team is null)
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            if (team.IsReady)
            {
                team.IsReady = false;
            }
            else
            {
                int playingCount = 0;
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Arena == arena
                        && otherPlayer.Freq == team.Freq
                        && otherPlayer.Ship != ShipType.Spec)
                    {
                        playingCount++;
                    }
                }

                if (playingCount < 1)
                {
                    _chat.SendMessage(player, "Your team has no players in game.");
                    return;
                }
                else if (playingCount > matchData.Configuration.PlayersPerTeam)
                {
                    _chat.SendMessage(player, $"Your team has too many players in game (current: {playingCount}, max: {matchData.Configuration.PlayersPerTeam}).");
                    return;
                }

                team.IsReady = true;
            }

            _chat.SendArenaMessage(arena, $"Freq {team.Freq} ({team.LeagueTeam?.TeamName}) is {(team.IsReady ? "READY" : "NOT READY")}.");

            // Check if all teams are ready.
            bool allReady = true;
            foreach (Team t in matchData.Teams)
            {
                if (!t.IsReady)
                {
                    allReady = false;
                    break;
                }
            }

            if (!allReady && matchData.Status == MatchStatus.StartingCountdown)
            {
                // The match was starting, stop it now that all teams are not ready.
                CancelLeagueStartupSequence(matchData);
            }
            else if (allReady && matchData.Status == MatchStatus.Initializing)
            {
                // All teams are ready, begin the startup sequence.
                BeginLeagueMatchStartupSequence(matchData);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                Forces a league match to start even if not all teams have readied up (?ready).
                This can be used if one team is holding things up and has gone beyond a time limit.
                """)]
        private void Command_forcestart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? matchData = arenaData.LeagueMatch;
            if (matchData is null)
                return;

            matchData.IsForcedStart = true;
            BeginLeagueMatchStartupSequence(matchData);
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "",
            Description = """
                Toggles whether a player is allowed to be a starter for the match. Captains only.
                Initially, all players on the roster are automatically allowed.
                Players not allowed will be sent to spectator mode and will not be able to enter a ship until toggled back.
                """)]
        private void Command_allowplay(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? leagueMatch = arenaData.LeagueMatch;
            if (leagueMatch is null)
                return;

            if (leagueMatch.Status >= MatchStatus.Starting)
            {
                _chat.SendMessage(player, "This command can only be used prior to match startup.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Team? team = playerData.CaptainOfTeam;
            if (team is null)
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                return;

            if (targetPlayer.Freq != team.Freq)
                return;

            if (team.NonStarter.Remove(targetPlayer.Name!))
            {
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                    players.Add(player);
                    players.Add(targetPlayer);

                    _chat.SendSetMessage(players, $"{CommandNames.AllowPlay}: '{targetPlayer.Name}' can play as a starter.");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
            else if (team.NonStarter.Add(targetPlayer.Name!))
            {
                if (targetPlayer.Ship != ShipType.Spec)
                {
                    _game.SetShip(targetPlayer, ShipType.Spec);
                }

                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                    players.Add(player);
                    players.Add(targetPlayer);

                    _chat.SendSetMessage(players, $"{CommandNames.AllowPlay}: '{targetPlayer.Name}' set to not play as a starter.");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<none> | <player name> | -a | -c",
            Description = $"""
                Toggles whether a member on a roster is allowed to sub into a league match. Captains only.
                
                This command can target a player by sending it as a private message: /?{CommandNames.AllowSub} 
                or :foo:?{CommandNames.AllowSub} (where the player's name is foo)

                Otherwise if not targeting a single player:
                * Use -a to allow all rostered players (rather than have to run the command individually on each).
                  This includes players that are not yet connected.
                * Use -c to clear the allowsub list.
                """)]
        private void Command_allowsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? leagueMatch = arenaData.LeagueMatch;
            if (leagueMatch is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Team? team = playerData.CaptainOfTeam;
            if (team is null)
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            if (team.LeagueTeam is null)
                return;

            ReadOnlySpan<char> playerName = [];
            string? playerNameStr = null;

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                playerName = playerNameStr = targetPlayer.Name;
            }

            if (playerName.IsEmpty)
            {
                if (parameters.IsEmpty)
                {
                    PrintUsage(player);
                    return;
                }

                if (parameters.StartsWith('-'))
                {
                    if (parameters.StartsWith("-a"))
                    {
                        if (team.LeagueTeam is not null)
                        {
                            foreach (string name in team.LeagueTeam.Roster.Keys)
                            {
                                team.AllowedToSub.Add(name);
                            }

                            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                            try
                            {
                                _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                                players.Add(player);

                                _chat.SendSetMessage(players, $"{CommandNames.AllowSub}: All players on the roster are allowed to sub.");
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(players);
                            }
                        }
                    }
                    else if (parameters.StartsWith("-c"))
                    {
                        if (team.LeagueTeam is not null)
                        {
                            if (team.LeagueTeam.Roster.Count == 0)
                            {
                                _chat.SendMessage(player, $"{CommandNames.AllowSub}: Already empty.");
                                return;
                            }

                            team.AllowedToSub.Clear();

                            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                            try
                            {
                                _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                                players.Add(player);

                                _chat.SendSetMessage(players, $"{CommandNames.AllowSub}: Cleared, no players are allowed to sub.");
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(players);
                            }
                        }
                    }
                    else
                    {
                        PrintUsage(player);
                    }

                    return;
                }
                else
                {
                    playerName = parameters;
                }
            }

            if (playerName.IsEmpty)
            {
                PrintUsage(player);
                return;
            }

            if (team.AllowedToSubLookup.Remove(playerName))
            {
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                    players.Add(player);

                    _chat.SendSetMessage(players, $"{CommandNames.AllowSub}: Removed {playerName}");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
            else
            {
                if (!team.LeagueTeam.Roster.TryGetAlternateLookup<ReadOnlySpan<char>>(out var rosterLookup))
                    return;

                if (!rosterLookup.ContainsKey(playerName))
                {
                    _chat.SendMessage(player, $"{CommandNames.AllowSub}: {playerName} is not on the roster.");
                    return;
                }

                playerNameStr ??= StringPool.Shared.GetOrAdd(playerName);
                team.AllowedToSub.Add(playerNameStr);

                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.TargetToSet(Target.TeamTarget(arena, team.Freq), players);
                    players.Add(player);

                    _chat.SendSetMessage(players, $"{CommandNames.AllowSub}: Added {playerName}");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }


            void PrintUsage(Player player)
            {
                _chat.SendMessage(player, $"Invalid input. For usage instructions, see: ?man {CommandNames.AllowSub}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "<none> | <slot #>",
            Description = """
                For league captains to toggle whether a slot is enabled for a full sub.
                This can be private messaged to a player that is currently assigned to a slot.
                For example: /?fullsub
                Alternatively, the slot # can specified as a parameter (slot numbers begin with 1):
                For example: ?fullsub 2
                """)]
        private void Command_fullsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            MatchData? matchData = arenaData.LeagueMatch;
            if (matchData is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            // Check that the player is a captain
            Team? team = playerData.CaptainOfTeam;
            if (team is null)
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            // Determine which slot to toggle.
            PlayerSlot? slot = null;
            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                    return;

                if (targetPlayerData.AssignedSlot is null)
                {
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not assigned a slot.");
                    return;
                }
                else if (targetPlayerData.AssignedSlot.Team != team)
                {
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not on your team.");
                    return;
                }

                slot = targetPlayerData.AssignedSlot;
            }

            if (slot is null && !parameters.IsEmpty)
            {
                if (int.TryParse(parameters, out int slotNumber))
                {
                    if (slotNumber < 1 || slotNumber > team.Slots.Length)
                    {
                        _chat.SendMessage(player, "Invalid slot.");
                        return;
                    }

                    slot = team.Slots[slotNumber];
                }
            }

            if (slot is null)
            {
                _chat.SendMessage(player, "You must specify a slot either by including the slot # or by private messaging the command to a player assigned to a slot.");
                return;
            }

            slot.FullSubEnabled = !slot.FullSubEnabled;

            _chat.SendMessage(player, $"Slot {slot.SlotIdx + 1} ({(string.IsNullOrEmpty(slot.PlayerName) ? "<unassigned>" : slot.PlayerName)}) is full sub {(slot.FullSubEnabled ? "ENABLED" : "DISABLED")}.");
        }

        #endregion

        #region IMatchmakingQueueAdvisor

        string? IMatchmakingQueueAdvisor.GetDefaultQueue(Arena arena)
        {
            if (!_arenaBaseDataDictionary.TryGetValue(arena.BaseName, out ArenaBaseData? arenaBaseData))
                return null;

            return arenaBaseData.DefaultQueueName;
        }

        bool IMatchmakingQueueAdvisor.TryGetCurrentMatchInfo(string playerName, StringBuilder matchInfo)
        {
            if (!_playerSlotDictionary.TryGetValue(playerName, out PlayerSlot? slot))
                return false;

            if (matchInfo is not null)
            {
                MatchData matchData = slot.MatchData;
                matchInfo.Append($"{slot.MatchData.MatchIdentifier.MatchType} - Arena:{matchData.ArenaName}");
                if (matchData.Configuration.Boxes.Length > 0)
                {
                    matchInfo.Append($" Box:{matchData.MatchIdentifier.BoxIdx + 1}");
                }
                matchInfo.Append($" Freq:{slot.Team.Freq}");
                matchInfo.Append($" Slot:{slot.SlotIdx}");
                if (!string.Equals(slot.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                {
                    matchInfo.Append(" (subbed out)");
                }
            }

            return true;
        }

        #endregion

        private async Task<bool> LoadConfigurationAsync()
        {
            ConfigHandle? ch = await _configManager.OpenConfigFileAsync(null, ConfigurationFileName).ConfigureAwait(false);
            if (ch is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Error opening {ConfigurationFileName}.");
                return false;
            }

            try
            {
                int abandonStartPenaltySeconds = _configManager.GetInt(ch, "General", "AbandonStartPenalty", 300);
                _abandonStartPenaltyDuration = TimeSpan.FromSeconds(abandonStartPenaltySeconds);

                int notReadyStartPenaltySeconds = _configManager.GetInt(ch, "General", "NotReadyStartPenalty", 180);
                _notReadyStartPenaltyDuration = TimeSpan.FromSeconds(notReadyStartPenaltySeconds);

                int i = 1;
                string? matchType;
                while (!string.IsNullOrWhiteSpace(matchType = _configManager.GetStr(ch, "Matchmaking", $"Match{i++}")))
                {
                    if (_matchConfigurationDictionary.TryGetValue(matchType, out MatchConfiguration? matchConfiguration))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Match {matchType} already exists. Check configuration for a duplicate.");
                        continue;
                    }

                    matchConfiguration = CreateMatchConfiguration(ch, matchType);
                    if (matchConfiguration is null)
                    {
                        continue;
                    }

                    // league
                    if (matchConfiguration.LeagueId is not null && matchConfiguration.GameTypeId is not null)
                    {
                        if (!_leagueMatchConfigurations.TryAdd(matchConfiguration.GameTypeId.Value, matchConfiguration))
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Unable to register game type {matchConfiguration.GameTypeId.Value} as a league match configuration for match type {matchConfiguration.MatchType}.");
                        }
                        else
                        {
                            if (_leagueManager is null)
                            {
                                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"No {nameof(ILeagueManager)} to register the {nameof(TeamVersusMatch)} module with for game type {matchConfiguration.GameTypeId.Value}.");
                            }
                            else
                            {
                                if (!_leagueManager.Register(matchConfiguration.GameTypeId.Value, this))
                                {
                                    _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Failed to register the {nameof(TeamVersusMatch)} module with for game type {matchConfiguration.GameTypeId.Value} on the {nameof(ILeagueManager)}.");
                                }
                            }
                        }
                    }

                    // matchmaking (queues)
                    string? queueName = matchConfiguration.QueueName;
                    if (!string.IsNullOrWhiteSpace(queueName))
                    {
                        if (!_queueDictionary.TryGetValue(queueName, out TeamVersusMatchmakingQueue? queue))
                        {
                            queue = CreateQueue(ch, queueName);
                            if (queue is null)
                                continue;

                            // TODO: for now only allowing groups of the exact size needed (for simplified matching)
                            if (queue.Options.AllowGroups
                                && (queue.Options.MinGroupSize != matchConfiguration.PlayersPerTeam
                                    || queue.Options.MaxGroupSize != matchConfiguration.PlayersPerTeam))
                            {
                                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Unsupported configuration for match '{matchConfiguration.MatchType}'. Queue '{queueName}' can't be used (must only allow groups of exactly {matchConfiguration.PlayersPerTeam} players).");
                                continue;
                            }

                            if (!_matchmakingQueues.RegisterQueue(queue))
                            {
                                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Failed to register queue '{queueName}' (used by match:{matchConfiguration.MatchType}).");
                                return false;
                            }

                            _queueDictionary.Add(queueName, queue);
                        }

                        if (!_queueMatchConfigurations.TryGetValue(queueName, out List<MatchConfiguration>? matchConfigurationList))
                        {
                            matchConfigurationList = new List<MatchConfiguration>(1);
                            _queueMatchConfigurations.Add(queueName, matchConfigurationList);
                        }

                        matchConfigurationList.Add(matchConfiguration);
                    }

                    _matchConfigurationDictionary.Add(matchType, matchConfiguration);

                    if (!_arenaBaseDataDictionary.TryGetValue(matchConfiguration.ArenaBaseName, out ArenaBaseData? arenaBaseData))
                    {
                        arenaBaseData = new()
                        {
                            DefaultQueueName = queueName
                        };

                        _arenaBaseDataDictionary.Add(matchConfiguration.ArenaBaseName, arenaBaseData);
                    }
                }
            }
            finally
            {
                _configManager.CloseConfigFile(ch);
            }

            return true;
        }

        private MatchConfiguration? CreateMatchConfiguration(ConfigHandle ch, string matchType)
        {
            long? gameTypeId;
            string? gameTypeIdStr = _configManager.GetStr(ch, matchType, "GameTypeId");
            if (string.IsNullOrWhiteSpace(gameTypeIdStr))
            {
                gameTypeId = null;
            }
            else if (long.TryParse(gameTypeIdStr, out long gameTypeIdLong))
            {
                gameTypeId = gameTypeIdLong;
            }
            else
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid GameTypeId for Match '{matchType}'.");
                return null;
            }

            long? leagueId;
            string? leagueIdStr = _configManager.GetStr(ch, matchType, "LeagueId");
            if (string.IsNullOrWhiteSpace(leagueIdStr))
            {
                leagueId = null;
            }
            else if (long.TryParse(leagueIdStr, out long leagueIdLong))
            {
                leagueId = leagueIdLong;
            }
            else
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid LeagueId for Match '{matchType}'.");
                return null;
            }

            string? queueName = _configManager.GetStr(ch, matchType, "Queue");
            if (string.IsNullOrWhiteSpace(queueName))
                queueName = null; // consider empty or whitespace to just be null (as if the setting was completely omitted)

            if (leagueId is null && queueName is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"No LeagueID or Queue for Match '{matchType}'.");
                return null;
            }

            if (leagueId is not null && queueName is not null)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Can't have both a LeagueId and a Queue for Match '{matchType}'.");
                return null;
            }

            if (leagueId is not null && gameTypeId is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Can't have a LeagueId without a GameTypeId for Match '{matchType}'.");
                return null;
            }

            string? arenaBaseName = _configManager.GetStr(ch, matchType, "ArenaBaseName");
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

            int arrivalWaitDurationSeconds = _configManager.GetInt(ch, matchType, "ArrivalWaitDuration", 45);
            TimeSpan arrivalWaitDuration = TimeSpan.FromSeconds(arrivalWaitDurationSeconds);
            if (arrivalWaitDuration < TimeSpan.Zero)
                arrivalWaitDuration = TimeSpan.Zero;

            int readyWaitDurationSeconds = _configManager.GetInt(ch, matchType, "ReadyWaitDuration", 15);
            TimeSpan readyWaitDuration = TimeSpan.FromSeconds(readyWaitDurationSeconds);
            if (readyWaitDuration < TimeSpan.Zero)
                readyWaitDuration = TimeSpan.Zero;

            int startCountdownDurationSeconds = _configManager.GetInt(ch, matchType, "StartCountdownDuration", 5);
            TimeSpan startCountdownDuration = TimeSpan.FromSeconds(startCountdownDurationSeconds);
            if (startCountdownDuration < TimeSpan.Zero)
                startCountdownDuration = TimeSpan.Zero;

            TimeSpan? timeLimit = null;
            string? timeLimitStr = _configManager.GetStr(ch, matchType, "TimeLimit");
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
            if (timeLimit is not null)
            {
                string? overTimeLimitStr = _configManager.GetStr(ch, matchType, "OverTimeLimit");
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

            string? winConditionDelayStr = _configManager.GetStr(ch, matchType, "WinConditionDelay");
            if (string.IsNullOrWhiteSpace(winConditionDelayStr)
                || !TimeSpan.TryParse(winConditionDelayStr, out TimeSpan winConditionDelay))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid WinConditionDelay for Match '{matchType}'.");
                return null;
            }

            string? inactiveTeamsMatchCompletionDelayStr = _configManager.GetStr(ch, matchType, "InactiveTeamsMatchCompletionDelay");
            if(string.IsNullOrWhiteSpace(inactiveTeamsMatchCompletionDelayStr)
                || !TimeSpan.TryParse(inactiveTeamsMatchCompletionDelayStr, out TimeSpan inactiveTeamsMatchCompletionDelay))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid InactiveTeamsMatchCompletionDelay for Match '{matchType}'.");
                return null;
            }

            int timeLimitWinBy = _configManager.GetInt(ch, matchType, "TimeLimitWinBy", 1);
            if (timeLimitWinBy < 1)
                timeLimitWinBy = 1;

            int maxLagOuts = _configManager.GetInt(ch, matchType, "MaxLagOuts", 3);

            string? allowShipChangeAfterDeathDurationStr = _configManager.GetStr(ch, matchType, "AllowShipChangeAfterDeathDuration");
            if (string.IsNullOrWhiteSpace(allowShipChangeAfterDeathDurationStr) 
                || !TimeSpan.TryParse(allowShipChangeAfterDeathDurationStr, out TimeSpan allowShipChangeAfterDeathDuration))
            {
                allowShipChangeAfterDeathDuration = TimeSpan.Zero;
            }

            string? allowShipChangeAfterSubDurationStr = _configManager.GetStr(ch, matchType, "AllowShipChangeAfterSubDuration");
            if (string.IsNullOrWhiteSpace(allowShipChangeAfterSubDurationStr)
                || !TimeSpan.TryParse(allowShipChangeAfterSubDurationStr, out TimeSpan allowShipChangeAfterSubDuration))
            {
                allowShipChangeAfterSubDuration = TimeSpan.Zero;
            }

            int maxShipChangesPerEligibleEvent = _configManager.GetInt(ch, matchType, "MaxShipChangesPerEligibleEvent", 1);

            int inactiveSlotAvailableDelaySeconds = _configManager.GetInt(ch, matchType, "InactiveSlotAvailableDelay", 30);
            TimeSpan inactiveSlotAvailableDelay = TimeSpan.FromSeconds(inactiveSlotAvailableDelaySeconds);

            string? deathSubDurationStr = _configManager.GetStr(ch, matchType, "DeathSubDuration");
            if (string.IsNullOrWhiteSpace(deathSubDurationStr)
                || !TimeSpan.TryParse(deathSubDurationStr, out TimeSpan deathSubDuration))
            {
                deathSubDuration = TimeSpan.Zero;
            }

            int initialFullSubs = _configManager.GetInt(ch, matchType, "InitialFullSubs", 0);

            bool subInMustUsePriorShip = _configManager.GetBool(ch, matchType, "SubInMustUsePriorShip", false);

            ItemsAction subInItemsAction = _configManager.GetEnum(ch, matchType, "SubInItemsAction", ItemsAction.Full);
            ItemsAction returnToMatchItemsAction = _configManager.GetEnum(ch, matchType, "ReturnToMatchItemsAction", ItemsAction.Burn);

            ItemsCommandOption itemsCommandOption = _configManager.GetEnum(ch, matchType, "ItemsCommandOption", ItemsCommandOption.None);

            bool burnItemsOnSpawn = _configManager.GetBool(ch, matchType, "BurnItemsOnSpawn", false);

            bool allowFillUnusedSlots = _configManager.GetBool(ch, matchType, "AllowFillUnusedSlots", true);

            string? replayRecordPath = _configManager.GetStr(ch, matchType, "ReplayRecordPath");

            if (!string.IsNullOrWhiteSpace(replayRecordPath) && numBoxes != 1)
            {
                // The replay module only supports recording one replay at a time per arena.
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Only one box is allowed when replay recording is enabled. (Match '{matchType}')");
                return null;
            }

            MatchConfiguration matchConfiguration = new()
            {
                MatchType = matchType,
                GameTypeId = gameTypeId,
                LeagueId = leagueId,
                QueueName = queueName,
                ArenaBaseName = arenaBaseName,
                MaxArenas = maxArenas,
                NumTeams = numTeams,
                PlayersPerTeam = playersPerTeam,
                LivesPerPlayer = livesPerPlayer,
                ArrivalWaitDuration = arrivalWaitDuration,
                ReadyWaitDuration = readyWaitDuration,
                StartCountdownDuration = startCountdownDuration,
                TimeLimit = timeLimit,
                OverTimeLimit = overTimeLimit,
                WinConditionDelay = winConditionDelay,
                InactiveTeamsMatchCompletionDelay = inactiveTeamsMatchCompletionDelay,
                TimeLimitWinBy = timeLimitWinBy,
                MaxLagOuts = maxLagOuts,
                AllowShipChangeAfterDeathDuration = allowShipChangeAfterDeathDuration,
                AllowShipChangeAfterSubDuration = allowShipChangeAfterSubDuration,
                MaxShipChangesPerEligibleEvent = maxShipChangesPerEligibleEvent,
                InactiveSlotAvailableDelay = inactiveSlotAvailableDelay,
                DeathSubDuration = deathSubDuration,
                InitialFullSubs = initialFullSubs,
                SubInMustUsePriorShip = subInMustUsePriorShip,
                SubInItemsAction = subInItemsAction,
                ReturnToMatchItemsAction = returnToMatchItemsAction,
                ItemsCommandOption = itemsCommandOption,
                BurnItemsOnSpawn = burnItemsOnSpawn,
                AllowFillUnusedSlots = allowFillUnusedSlots,
                ReplayRecordPath = replayRecordPath,
                Boxes = new MatchBoxConfiguration[numBoxes],
            };

            if (!LoadMatchBoxesConfiguration(ch, matchType, matchConfiguration))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid configuration of boxes for Match '{matchType}'.");
                return null;
            }

            return matchConfiguration;

            bool LoadMatchBoxesConfiguration(ConfigHandle ch, string matchType, MatchConfiguration matchConfiguration)
            {
                List<TileCoordinates> tempCoordList = [];

                for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                {
                    int boxNumber = boxIdx + 1; // configuration is 1 based
                    string boxSection = $"{matchType}-Box{boxNumber}";

                    string? playAreaMapRegion = _configManager.GetStr(ch, boxSection, "PlayAreaMapRegion");

                    MatchBoxConfiguration boxConfiguration = new()
                    {
                        TeamStartLocations = new TileCoordinates[matchConfiguration.NumTeams][],
                        PlayAreaMapRegion = playAreaMapRegion,
                    };

                    // Start locations
                    for (int teamIdx = 0; teamIdx < matchConfiguration.NumTeams; teamIdx++)
                    {
                        int teamNumber = teamIdx + 1;

                        if (tempCoordList.Count != 0)
                            tempCoordList.Clear();

                        int coordId = 1;
                        string? coordStr;
                        while (!string.IsNullOrWhiteSpace(coordStr = _configManager.GetStr(ch, boxSection, $"Team{teamNumber}StartLocation{coordId}")))
                        {
                            if (!TileCoordinates.TryParse(coordStr, out TileCoordinates coordinates))
                            {
                                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Invalid starting location for Match '{matchType}', Box:{boxNumber}, Team:{teamNumber}, #:{coordId}.");
                                continue;
                            }
                            else
                            {
                                tempCoordList.Add(coordinates);
                            }

                            coordId++;
                        }

                        if (tempCoordList.Count <= 0)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Missing starting location for Match '{matchType}', Box:{boxNumber}, Team:{teamNumber}.");
                            return false;
                        }

                        boxConfiguration.TeamStartLocations[teamIdx] = [.. tempCoordList];
                    }

                    // Spawn overrides
                    if (!LoadSpawnOverrides(matchType, boxNumber, boxSection, boxConfiguration))
                        return false;

                    matchConfiguration.Boxes[boxIdx] = boxConfiguration;
                }

                return true;

                bool LoadSpawnOverrides(string matchType, int boxNumber, string boxSection, MatchBoxConfiguration boxConfiguration)
                {
                    Span<char> xKey = stackalloc char["Spawn-Team#-X".Length];
                    Span<char> yKey = stackalloc char["Spawn-Team#-Y".Length];
                    Span<char> rKey = stackalloc char["Spawn-Team#-Radius".Length];

                    "Spawn-Team#-X".CopyTo(xKey);
                    "Spawn-Team#-Y".CopyTo(yKey);
                    "Spawn-Team#-Radius".CopyTo(rKey);

                    for (int freqIdx = 0; freqIdx < 4; freqIdx++)
                    {
                        xKey[10] = yKey[10] = rKey[10] = (char)('0' + freqIdx);

                        int x = _configManager.GetInt(ch, boxSection, xKey, -1);
                        int y = _configManager.GetInt(ch, boxSection, yKey, -1);
                        int r = _configManager.GetInt(ch, boxSection, rKey, -1);

                        if (x == -1 || y == -1 || r == -1)
                        {
                            boxConfiguration.SpawnPositions[freqIdx] = null;
                            continue;
                        }

                        if (!IsValidValue(boxSection, xKey, x, 0, 1023))
                            return false;

                        if (!IsValidValue(boxSection, yKey, y, 0, 1023))
                            return false;

                        if (!IsValidValue(boxSection, rKey, r, 0, 511))
                            return false;

                        boxConfiguration.SpawnPositions[freqIdx] = new() { X = (ushort)x, Y = (ushort)y, Radius = (ushort)r };
                    }

                    return true;

                    bool IsValidValue(ReadOnlySpan<char> section, ReadOnlySpan<char> key, int value, int min, int max)
                    {
                        if (value < min || value > max)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid spawn setting {section}:{key} (value={value}, Min={min}, Max={max}).");
                            return false;
                        }

                        return true;
                    }
                }
            }
        }

        private TeamVersusMatchmakingQueue? CreateQueue(ConfigHandle ch, string queueName)
        {
            string queueSection = $"Queue-{queueName}";

            string? description = _configManager.GetStr(ch, queueSection, "Description");

            bool allowSolo = _configManager.GetInt(ch, queueSection, "AllowSolo", 0) != 0;
            bool allowGroups = _configManager.GetInt(ch, queueSection, "AllowGroups", 0) != 0;

            if (!allowSolo && !allowGroups)
            {
                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Invalid configuration for queue:{queueName}. It doesn't allow solo players or groups. Skipping.");
                return null;
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

            bool allowAutoRequeue = _configManager.GetInt(ch, queueSection, "AllowAutoRequeue", 0) != 0;

            QueueOptions options = new()
            {
                AllowSolo = allowSolo,
                AllowGroups = allowGroups,
                MinGroupSize = minGroupSize,
                MaxGroupSize = maxGroupSize,
                AllowAutoRequeue = allowAutoRequeue,
            };

            return new TeamVersusMatchmakingQueue(queueName, options, description);
        }

        private void BeginLeagueMatchStartupSequence(MatchData matchData)
        {
            if (matchData.Status != MatchStatus.Initializing)
                return;

            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            LeagueGameInfo? leagueGame = matchData.LeagueGame;
            if (leagueGame is null)
                return;

            // Set starting locations.
            foreach (Team team in matchData.Teams)
            {
                SetTeamStartLoocation(team);
            }

            // Move any players not in the match to spec.
            // Warp players in the match to their starting locations and reset ships.
            foreach (Player player in _playerData.Players)
            {
                if (player.Arena != matchData.Arena)
                    continue;

                if (player.Ship == ShipType.Spec && player.Freq == arena.SpecFreq)
                    continue;

                Team? team = null;
                foreach (Team otherTeam in matchData.Teams)
                {
                    if (otherTeam.Freq == player.Freq)
                    {
                        team = otherTeam;
                        break;
                    }
                }

                if (team is null)
                {
                    _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                }
                else
                {
                    TileCoordinates startLocation = GetTeamStartLoocation(team);
                    _game.WarpTo(player, startLocation.X, startLocation.Y);
                    _game.ShipReset(player);
                }
            }

            // Begin the countdown.
            matchData.Status = MatchStatus.StartingCountdown;
            matchData.StartCountdown = (int)matchData.Configuration.StartCountdownDuration.TotalSeconds;
            SetProcessMatchStateTimer(matchData);

            // Record replay if configured
            if (_replayController is not null && !string.IsNullOrWhiteSpace(matchData.Configuration.ReplayRecordPath))
            {
                arenaData.ReplayRecordingFilePath = Path.Join(matchData.Configuration.ReplayRecordPath, $"{leagueGame.LeagueId}/{leagueGame.SeasonId}/{DateTime.UtcNow:yyyyMMdd-HHmmss} {leagueGame.SeasonGameId}.replay");

                if (!_replayController.StartRecording(
                    arena,
                    arenaData.ReplayRecordingFilePath,
                    $"League: {leagueGame.LeagueName}, Season: {leagueGame.SeasonName}, SeasonGameId: {leagueGame.SeasonGameId}"))
                {
                    _logManager.LogA(LogLevel.Warn, nameof(TeamVersusMatch), arena, $"Failed to start recording of league match. (SeasonGameId: {leagueGame.SeasonGameId}, FilePath: {arenaData.ReplayRecordingFilePath})");
                    arenaData.ReplayRecordingFilePath = null;
                }

                // TODO: Wait for the recording to actually begin? Would need some mechanism added to the replay module to detect it.
                // Perhaps use callbacks? There's also the possiblity it fails to begin recording (bad filename, out of disk space, etc..)
                // For now, not dealing with it and assuming if recording starts, it most likely will happen by the time the GO! occurs.
                // The following arena notifications unfortunately will very likely not make it into the recording.
            }

            // Send arena notifications.
            _chat.SendArenaMessage(arena, $"All teams are ready. Starting in {matchData.StartCountdown} seconds!");

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                sb.Append($"{leagueGame.LeagueName} - {leagueGame.SeasonName}");

                if (!string.IsNullOrWhiteSpace(leagueGame.RoundName))
                {
                    sb.Append($" - {leagueGame.RoundName}");
                }
                else if (leagueGame.RoundNumber is not null)
                {
                    sb.Append($" - Round #{leagueGame.RoundNumber.Value}");
                }

                _chat.SendArenaMessage(arena, sb);

                sb.Clear();

                foreach (Team team in matchData.Teams)
                {
                    if (sb.Length > 0)
                        sb.Append(" vs ");

                    if (team.LeagueTeam is null)
                        sb.Append(team.Freq);
                    else
                        sb.Append(team.LeagueTeam.TeamName);
                }

                _chat.SendArenaMessage(arena, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            // TODO: The current SVS league startup countdown is 60 seconds, which is very long.
            // This is probably to make sure any existing bricks expire?
            // We could force bricks to disappear by temporarily overriding the Brick:BrickTime client setting to 0 or 1 and then unoverride it.
            // Then the countdown could be reduced.
            //_clientSettings.OverrideSetting(arena, BrickTimeClientSettingIdentifier, 0); // not sure if 0 is valid, if not then 1 definitely is, but then might need a short delay before unoverride?
            //_clientSettings.SendClientSettings(arena); // would be a good method to add
            //_clientSettings.UnoverrideSetting(arena, BrickTimeClientSettingIdentifier); 
            //_clientSettings.SendClientSettings(arena); // might need to put a slight delay on sending this?
        }

        private void CancelLeagueStartupSequence(MatchData matchData)
        {
            if (matchData.Status != MatchStatus.StartingCountdown)
                return;

            LeagueGameInfo? leagueGame = matchData.LeagueGame;
            if (leagueGame is null)
                return;

            matchData.Status = MatchStatus.Initializing;
            _mainloopTimer.ClearTimer<MatchData>(MainloopTimer_ProcessMatchStateChange, matchData);

            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!string.IsNullOrWhiteSpace(arenaData.ReplayRecordingFilePath))
            {
                StopRecordingReplay(arena, arenaData);

                // TODO: Unfortunately, we can't delete the recording, because the task recording it may not have ended yet.
            }
        }

        private void StopRecordingReplay(Arena arena, ArenaData arenaData)
        {
            if (string.IsNullOrWhiteSpace(arenaData.ReplayRecordingFilePath))
                return;

            if (_replayController is null)
                return;

            if (!_replayController.StopRecording(arena))
            {
                _logManager.LogA(LogLevel.Warn, nameof(TeamVersusMatch), arena, $"Failed to stop recording of match. (FilePath: {arenaData.ReplayRecordingFilePath})");
            }

            arenaData.ReplayRecordingFilePath = null;
        }

        private TileCoordinates SetTeamStartLoocation(Team team)
        {
            MatchData matchData = team.MatchData;
            TileCoordinates[] startLocations = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[team.TeamIdx];
            team.StartLocationIdx = startLocations.Length == 1 ? 0 : _prng.Number(0, startLocations.Length - 1);
            return startLocations[team.StartLocationIdx];
        }

        private static TileCoordinates GetTeamStartLoocation(Team team)
        {
            MatchData matchData = team.MatchData;
            return matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[team.TeamIdx][team.StartLocationIdx];
        }

        private bool MakeMatch(TeamVersusMatchmakingQueue queue)
        {
            if (queue is null)
                return false;

            if (!_queueMatchConfigurations.TryGetValue(queue.Name, out List<MatchConfiguration>? matchConfigurations))
                return false;

            // Most often, a queue will be used by a single match type. However, multiple match types can use the same queue.
            // An example of where this may be used is if there are multiple maps that can be used for a particular game type.
            // Here, we randomize which match type to start searching.
            int startingIndex = matchConfigurations.Count == 1 ? 0 : _prng.Number(0, matchConfigurations.Count);
            for (int i = 0; i < matchConfigurations.Count; i++)
            {
                MatchConfiguration matchConfiguration = matchConfigurations[(startingIndex + i) % matchConfigurations.Count];

                //
                // Find the next available location for a game to be played on.
                //

                if (!TryGetAvailableMatch(matchConfiguration, out MatchData? matchData))
                    continue;

                //
                // Found an available location for a game to be played in. Next, try to find players.
                //

                List<TeamLineup> teamList = _teamLineupListPool.Get();
                for (int teamIdx = 0; teamIdx < matchConfiguration.NumTeams; teamIdx++)
                {
                    teamList.Add(_teamLineupPool.Get());
                }

                List<Player> participantList = _playerListPool.Get();

                try
                {
                    if (!queue.GetParticipants(matchData.Configuration, teamList, participantList))
                    {
                        foreach (TeamLineup teamLineup in teamList)
                        {
                            _teamLineupPool.Return(teamLineup);
                        }

                        _teamLineupListPool.Return(teamList);

                        continue;
                    }

                    //
                    // Reserve the match.
                    //

                    matchData.Status = MatchStatus.Initializing;

                    //
                    // Mark the players as playing.
                    //

                    HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        foreach (Player player in participantList)
                        {
                            players.Add(player);

                            // Add the participants in the order provided (which is the order they were queued up in).
                            matchData.ParticipationList.Add(new PlayerParticipationRecord(player.Name!, false, false));
                        }

                        _matchmakingQueues.SetPlaying(players);
                        _chat.SendAnyMessage(players, ChatMessageType.RemotePrivate, ChatSound.None, null, $"{_matchmakingQueues.NextCommandName}: Placing you into a {matchData.MatchIdentifier.MatchType} match.");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(players);
                    }
                }
                finally
                {
                    _playerListPool.Return(participantList);
                }

                //
                // Initialize the match.
                //

                _ = InitializeMatch(matchData, teamList);

                return true;
            }

            return false;


            // local function that performs the steps required to initialize a match
            async Task InitializeMatch(MatchData matchData, List<TeamLineup> teamLineups)
            {
                try
                {
                    // Balance or randomize teams.
                    List<TeamLineup> mutableTeams = _teamLineupListPool.Get();
                    try
                    {
                        foreach (TeamLineup team in teamLineups)
                        {
                            if (!team.IsPremade)
                                mutableTeams.Add(team);
                        }

                        if (mutableTeams.Count >= 2)
                        {
                            bool balanced = false;

                            if (_teamVersusStatsBehavior is not null)
                            {
                                balanced = await _teamVersusStatsBehavior.BalanceTeamsAsync(matchData.Configuration, mutableTeams);
                            }

                            if (!balanced)
                            {
                                RandomizeTeams(matchData.Configuration, mutableTeams);
                            }
                        }
                    }
                    finally
                    {
                        _teamLineupListPool.Return(mutableTeams);
                    }

                    // Get the Player objects of all the players in the match.
                    Dictionary<string, Player> playersByName = new(matchData.Configuration.NumTeams * matchData.Configuration.PlayersPerTeam); // TODO: pool
                    HashSet<string> abandonedPlayerNames = _objectPoolManager.NameHashSetPool.Get();

                    try
                    {
                        foreach (TeamLineup lineup in teamLineups)
                        {
                            foreach ((string playerName, _) in lineup.Players)
                            {
                                Player? player = _playerData.FindPlayer(playerName);

                                if (player is not null)
                                    playersByName.Add(playerName, player);
                                else
                                    abandonedPlayerNames.Add(playerName);
                            }
                        }

                        if (abandonedPlayerNames.Count > 0)
                        {
                            // We're missing at least one player (disconnected), so we can't start. Cancel out.

                            // Send chat notifications.
                            HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                            try
                            {
                                notifyPlayers.UnionWith(playersByName.Values);

                                SendMatchCancellationNotification(notifyPlayers, abandonedPlayerNames, null, "abandoned the game");
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                            }

                            // End the match and perform cleanup to allow another match to start.
                            EndMatch(matchData, MatchEndReason.Cancelled, null);

                            // Unset players that were found such that they get added back to the queues in their original positions.
                            _matchmakingQueues.UnsetPlayingDueToCancel(playersByName.Values);

                            // For the players that disconnected, penalize with a delay.
                            _matchmakingQueues.UnsetPlayingAfterDelay(abandonedPlayerNames, _abandonStartPenaltyDuration);

                            return;
                        }
                    }
                    finally
                    {
                        _objectPoolManager.NameHashSetPool.Return(abandonedPlayerNames);
                    }

                    // Try to find the arena.
                    Arena? arena = _arenaManager.FindArena(matchData.ArenaName); // This will only find the arena if it already exists and is running.

                    // Assign players to their slots and send them to the arena.
                    for (int teamIdx = 0; teamIdx < teamLineups.Count; teamIdx++)
                    {
                        TeamLineup teamLineup = teamLineups[teamIdx];
                        Team team = matchData.Teams[teamIdx];

                        int slotIdx = 0;
                        foreach ((string playerName, int? premadeGroupId) in teamLineup.Players)
                        {
                            Player player = playersByName[playerName];
                            PlayerSlot slot = team.Slots[slotIdx++];
                            AssignSlot(slot, player);
                            slot.Status = PlayerSlotStatus.Waiting;
                            slot.PremadeGroupId = premadeGroupId;

                            if (arena is null || player.Arena != arena)
                            {
                                _arenaManager.SendToArena(player, matchData.ArenaName, 0, 0);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (TeamLineup lineup in teamLineups)
                    {
                        _teamLineupPool.Return(lineup);
                    }

                    _teamLineupListPool.Return(teamLineups);
                }

                if (_teamVersusStatsBehavior is not null)
                {
                    // Initialize the stats asynchronously.
                    await _teamVersusStatsBehavior.InitializeAsync(matchData);
                }

                MatchStartingCallback.Fire(_broker, matchData);

                // Set the time limit for players to arrive.
                matchData.PhaseExpiration = DateTime.UtcNow + matchData.Configuration.ArrivalWaitDuration;

                // Start the timer that will move the match through its states.
                SetProcessMatchStateTimer(matchData);
            }

            // local function that randomizes team members
            void RandomizeTeams(
                IMatchConfiguration matchConfiguration,
                IReadOnlyList<TeamLineup> teamList)
            {
                if (matchConfiguration is null || teamList is null)
                    return;

                int playersPerTeam = matchConfiguration.PlayersPerTeam;

                string[] playerNames = ArrayPool<string>.Shared.Rent(teamList.Count * playersPerTeam);
                try
                {
                    // Put the names into an array and clear each team's players.
                    int playerCount = 0;
                    foreach (TeamLineup teamLineup in teamList)
                    {
                        foreach ((string playerName, _) in teamLineup.Players)
                        {
                            playerNames[playerCount++] = playerName;
                        }

                        teamLineup.Players.Clear();
                    }

                    // Shuffle the array.
                    Span<string> playerNameSpan = playerNames.AsSpan(0, playerCount);
                    _prng.Shuffle(playerNameSpan);

                    // Assign players to each team from the randomized array.
                    int teamIdx = 0;
                    foreach (string playerName in playerNameSpan)
                    {
                        TeamLineup teamLineup = teamList[teamIdx];
                        while (teamLineup.Players.Count >= playersPerTeam)
                            teamLineup = teamList[++teamIdx];

                        teamLineup.Players.Add(playerName, null);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(playerNames);
                }
            }
        }

        /// <summary>
        /// Finds an available match (arena/box) for a match (matchmaking system).
        /// </summary>
        /// <param name="matchConfiguration"></param>
        /// <param name="matchData"></param>
        /// <returns></returns>
        private bool TryGetAvailableMatch(MatchConfiguration matchConfiguration, [MaybeNullWhen(false)] out MatchData matchData)
        {
            if (!_arenaBaseDataDictionary.TryGetValue(matchConfiguration.ArenaBaseName, out ArenaBaseData? arenaBaseData))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Missing ArenaBaseData for {matchConfiguration.ArenaBaseName}.");
                matchData = null;
                return false;
            }

            for (int arenaNumber = 0; arenaNumber < matchConfiguration.MaxArenas; arenaNumber++)
            {
                for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                {
                    MatchIdentifier matchIdentifier = new(matchConfiguration.MatchType, arenaNumber, boxIdx);
                    if (!_matchDataDictionary.TryGetValue(matchIdentifier, out matchData))
                    {
                        short startingFreq = arenaBaseData.NextFreq;
                        arenaBaseData.NextFreq += (short)matchConfiguration.NumTeams;

                        matchData = new MatchData(matchIdentifier, matchConfiguration, startingFreq);
                        matchData.Arena = _arenaManager.FindArena(matchData.ArenaName);
                        _matchDataDictionary.Add(matchIdentifier, matchData);
                        return true;
                    }

                    if (matchData.Status == MatchStatus.None)
                    {
                        // Found an existing, available match to use.
                        return true;
                    }
                }
            }

            // no availability
            matchData = null;
            return false;
        }

        /// <summary>
        /// Finds an available match (arena/box) for a match (league system).
        /// </summary>
        /// <param name="matchConfiguration"></param>
        /// <param name="leagueGame"></param>
        /// <param name="matchData"></param>
        /// <returns></returns>
        private bool TryGetAvailableMatch(MatchConfiguration matchConfiguration, LeagueGameInfo leagueGame, [MaybeNullWhen(false)] out MatchData matchData)
        {
            for (int arenaNumber = 0; arenaNumber < matchConfiguration.MaxArenas; arenaNumber++)
            {
                for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                {
                    MatchIdentifier matchIdentifier = new(matchConfiguration.MatchType, arenaNumber, boxIdx);
                    if (_matchDataDictionary.ContainsKey(matchIdentifier))
                        continue; // already in use

                    matchData = new MatchData(matchIdentifier, matchConfiguration, leagueGame);
                    matchData.Arena = _arenaManager.FindArena(matchData.ArenaName);

                    if (matchData.Arena is not null)
                    {
                        if (_arenaDataDictionary.TryGetValue(matchData.Arena, out ArenaData? arenaData))
                            arenaData.LeagueMatch = matchData;
                    }

                    _matchDataDictionary.Add(matchIdentifier, matchData);
                    return true;
                }
            }

            // no availability
            matchData = null;
            return false;
        }

        /// <summary>
        /// Notifies players that their match is being cancelled.
        /// </summary>
        /// <param name="players">The players to notify that the match is being cancelled.</param>
        /// <param name="responsiblePlayerNames">The names of the players responsible for the cancellation.</param>
        /// <param name="penalizePlayers">The players to notify that they are being penalized.</param>
        /// <param name="reason">The reason for the cancellation.</param>
        private void SendMatchCancellationNotification(
            HashSet<Player> players,
            HashSet<string> responsiblePlayerNames,
            HashSet<Player>? penalizePlayers,
            string reason)
        {
            StringBuilder messageBuilder = _objectPoolManager.StringBuilderPool.Get();
            StringBuilder responsibleBuilder = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                //
                // Prepare the notification messages.
                //

                messageBuilder.Append($"The match has been cancelled because {responsiblePlayerNames.Count} player{(responsiblePlayerNames.Count > 1 ? "s" : "")} {reason}:");

                foreach (string playerName in responsiblePlayerNames)
                {
                    responsibleBuilder.AppendCommaDelimited(playerName);
                }

                //
                // Notify
                //

                foreach (Player player in players)
                {
                    _chat.SendMessage(player, messageBuilder);
                    _chat.SendWrappedText(player, responsibleBuilder);
                }

                if (penalizePlayers is not null)
                {
                    foreach (Player player in penalizePlayers)
                    {
                        _chat.SendMessage(player, "You are being penalized and must wait for it to expire to play in another match.");
                    }
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(messageBuilder);
                _objectPoolManager.StringBuilderPool.Return(responsibleBuilder);
            }
        }

        private static void ResetSlotForMatchStart(PlayerSlot slot)
        {
            slot.Lives = slot.MatchData.Configuration.LivesPerPlayer;
            slot.LagOuts = 0;
            slot.AllowShipChangeExpiration = null;
        }

        private void AssignSlot(PlayerSlot slot, Player player)
        {
            if (slot is null || player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.AssignedSlot is not null)
            {
                // The player is already assigned a slot (this shouldn't happen).
                if (playerData.AssignedSlot == slot)
                {
                    // The slot is already assigned to the player.
                    return;
                }
                else
                {
                    // Unassign the player's old slot first.
                    UnassignSlot(playerData.AssignedSlot);
                }
            }

            if (slot.SubPlayer is not null)
            {
                // There is a sub in progress for the slot. Cancel it.
                // Note: If the current assignment is for a sub, this would have been cleared already.
                CancelSubInProgress(slot, true);
            }

            UnassignSlot(slot);

            // Assign the slot.
            _playerSlotDictionary.Add(player.Name!, slot);
            slot.PlayerName = player.Name;
            slot.Player = player;
            playerData.AssignedSlot = slot;

            // Clear fields now that the slot is newly assigned.
            slot.LagOuts = 0;
            slot.IsSubRequested = false;

            MatchAddPlayingCallback.Fire(_broker, slot.MatchData, player.Name!, player);
        }

        private void UnassignSlot(PlayerSlot slot)
        {
            if (slot is null)
                return;

            string? playerName = slot.PlayerName;
            if (string.IsNullOrWhiteSpace(playerName))
                return; // The slot is not assigned to a player.

            slot.PlayerName = null;

            Player? player = slot.Player;
            if (player is not null)
            {
                if (player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                {
                    playerData.AssignedSlot = null;
                }

                slot.Player = null;
            }

            MatchRemovePlayingCallback.Fire(_broker, slot.MatchData, playerName, player);
        }

        private void SetSlotInactive(PlayerSlot slot, SlotInactiveReason reason)
        {
            if (slot.MatchData.Status != MatchStatus.InProgress)
                return;

            if (slot.Status != PlayerSlotStatus.Playing)
                return;

            slot.Status = PlayerSlotStatus.Waiting;
            slot.InactiveTimestamp = DateTime.UtcNow;
            slot.LagOuts++;

            MatchData matchData = slot.MatchData;

            Arena? arena = matchData.Arena;
            if (arena is not null) // This should always be true
            {
                TeamVersusMatchPlayerLagOutCallback.Fire(arena, slot, reason);
            }

            if (slot.SubPlayer is null)
            {
                // There was no sub in progress. Update the player's participation record as such.
                for (int index = 0; index < matchData.ParticipationList.Count; index++)
                {
                    if (string.Equals(matchData.ParticipationList[index].PlayerName, slot.PlayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchData.ParticipationList[index] = matchData.ParticipationList[index] with { LeftWithoutSub = true };
                        break;
                    }
                }
            }

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(matchData, players);

                if (slot.CanBeSubbed)
                {
                    if (slot.SubPlayer is not null)
                    {
                        // The current player requested to be subbed out and another player was waiting (for the current player to hit max energy or leave) to be subbed in. 
                        SubSlot(slot);
                    }
                    else
                    {
                        // The slot is immediately available to sub.
                        if (slot.AvailableSubSlotNode.List is null)
                        {
                            _availableSubSlots.AddLast(slot.AvailableSubSlotNode);
                        }

                        if (reason == SlotInactiveReason.ChangedToSpec)
                        {
                            _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode. The slot is now available for ?{CommandNames.Sub}.");
                        }
                        else if (reason == SlotInactiveReason.LeftArena)
                        {
                            _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena. The slot is now available for ?{CommandNames.Sub}.");
                        }

                        SendSubAvailabilityNotificationToQueuedPlayers(matchData);
                    }
                }
                else
                {
                    // The slot will become available to sub if the player doesn't return in time.
                    if (reason == SlotInactiveReason.ChangedToSpec)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode and has {matchData.Configuration.InactiveSlotAvailableDelay.TotalSeconds} seconds to return before the slot is opened to ?{CommandNames.Sub}. [Lagouts: {slot.LagOuts}]");
                    }
                    else if (reason == SlotInactiveReason.LeftArena)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena and has {matchData.Configuration.InactiveSlotAvailableDelay.TotalSeconds} seconds to return before the slot is opened to ?{CommandNames.Sub}. [Lagouts: {slot.LagOuts}]");
                    }

                    _mainloopTimer.SetTimer(MainloopTimer_ProcessInactiveSlot, (int)matchData.Configuration.InactiveSlotAvailableDelay.TotalMilliseconds, Timeout.Infinite, slot, slot);
                }

                // Check if the team no longer has any active players.
                bool hasActiveTeammates = false;
                foreach (PlayerSlot teammateSlot in slot.Team.Slots)
                {
                    if (teammateSlot.Status == PlayerSlotStatus.Playing)
                    {
                        hasActiveTeammates = true;
                        break;
                    }
                }

                if (!hasActiveTeammates)
                {
                    _chat.SendSetMessage(players, $"Freq {slot.Team.Freq} has no active players remaining.");

                    // Check if there is more than one team with active players.
                    int remainingTeamCount = 0;
                    foreach (Team team in matchData.Teams)
                    {
                        bool hasActivePlayer = false;
                        foreach (PlayerSlot otherSlot in team.Slots)
                        {
                            if (slot.Status == PlayerSlotStatus.Playing)
                            {
                                hasActivePlayer = true;
                                break;
                            }
                        }

                        if (hasActivePlayer)
                        {
                            remainingTeamCount++;
                        }
                    }

                    if (remainingTeamCount < 2)
                    {
                        if (remainingTeamCount == 0)
                            _chat.SendSetMessage(players, $"There are no remaining active teams. The game with automatically end in {matchData.Configuration.InactiveTeamsMatchCompletionDelay.TotalSeconds} seconds if not refilled.");
                        else
                            _chat.SendSetMessage(players, $"There is a single remaining active team. The game with automatically end in {matchData.Configuration.InactiveTeamsMatchCompletionDelay.TotalSeconds} seconds if not refilled.");

                        // Schedule a timer to check for match completion.
                        // The timer is to allow player(s) to ?return before the check happens. (e.g. server lag spike kicking all players to spec)
                        ScheduleCheckForMatchCompletion(matchData, (int)matchData.Configuration.InactiveTeamsMatchCompletionDelay.TotalMilliseconds);
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        private void SendSubAvailabilityNotificationToQueuedPlayers(MatchData matchData)
        {
            string? queueName = matchData.Configuration.QueueName;
            if (queueName is null)
                return;

            if (!_queueDictionary.TryGetValue(queueName, out var queue))
                return;

            // Notify any solo players waiting in the queue that there is a slot available.
            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                queue.GetQueued(players, null);

                if (players.Count > 0)
                {
                    _chat.SendSetMessage(players, $"A slot in an ongoing {matchData.Configuration.MatchType} match is available to be subbed. To sub use: ?{CommandNames.Sub} {matchData.Configuration.MatchType}");
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        private bool MainloopTimer_ProcessInactiveSlot(PlayerSlot slot)
        {
            if (slot is null)
                return false;

            if (slot.Status != PlayerSlotStatus.Waiting)
                return false;

            // The player assigned to the slot has not returned in time. Therefore, it is now available for ?sub.
            // The player can still ?return before another player takes the slot.

            if (slot.AvailableSubSlotNode.List is null)
            {
                _availableSubSlots.AddLast(slot.AvailableSubSlotNode);
            }

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(slot.MatchData, players);

                _chat.SendSetMessage(players, $"{slot.PlayerName}'s slot is now available for ?{CommandNames.Sub}.");
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }

            SendSubAvailabilityNotificationToQueuedPlayers(slot.MatchData);

            return false;
        }

        /// <summary>
        /// Gets the players that should be notified about a match.
        /// This includes the players in the match and players spectating the match.
        /// </summary>
        /// <param name="matchData">The match.</param>
        /// <param name="players">The collection to populate with players.</param>
        private void GetPlayersToNotify(MatchData matchData, HashSet<Player> players)
        {
            if (matchData is null || players is null)
                return;

            if (matchData.LeagueGame is not null)
            {
                // It's a league game, so notify the entire arena.
                Arena? arena = matchData.Arena;
                if (arena is null)
                    return;

                _playerData.TargetToSet(arena, players);
            }
            else
            {
                // Players that are playing the match or are spectating the match, and are in the match's arena.
                _matchFocus.TryGetPlayers(matchData, players, MatchFocusReasons.Playing | MatchFocusReasons.Spectating, matchData.Arena);
            }
        }

        private void ProcessMatchStateChange(MatchData matchData)
        {
            if (matchData is null)
                return;

            _mainloop.QueueMainWorkItem(MainloopWork_ProcessMatchStateChange, matchData);
        }

        private void SetProcessMatchStateTimer(MatchData matchData)
        {
            _mainloopTimer.ClearTimer<MatchData>(MainloopTimer_ProcessMatchStateChange, matchData);
            _mainloopTimer.SetTimer(MainloopTimer_ProcessMatchStateChange, 1000, 1000, matchData, matchData);
        }

        private bool MainloopTimer_ProcessMatchStateChange(MatchData matchData)
        {
            if (matchData is null)
                return false;

            switch (matchData.Status)
            {
                case MatchStatus.Initializing:
                case MatchStatus.StartingCheck:
                case MatchStatus.StartingCountdown:
                case MatchStatus.Starting:
                case MatchStatus.InProgress:
                case MatchStatus.Complete:
                    MainloopWork_ProcessMatchStateChange(matchData);
                    break;

                case MatchStatus.None:
                default:
                    return false;
            }

            // Only continue the timer if the match is still in a valid state.
            return matchData.Status switch
            {
                MatchStatus.Initializing or MatchStatus.StartingCheck or MatchStatus.StartingCountdown or MatchStatus.Starting or MatchStatus.InProgress or MatchStatus.Complete => true,
                _ => false,
            };
        }

        private void MainloopWork_ProcessMatchStateChange(MatchData matchData)
        {
            Debug.Assert(_mainloop.IsMainloop);

            switch (matchData.Status)
            {
                case MatchStatus.Initializing:
                    ProcessInitializing(matchData);
                    break;

                case MatchStatus.StartingCheck:
                    ProcessStartingCheck(matchData);
                    break;

                case MatchStatus.StartingCountdown:
                    ProcessStartingCountdown(matchData);
                    break;

                case MatchStatus.Starting:
                    // Do nothing, the match is starting up asynchronously.
                    break;

                case MatchStatus.InProgress:
                    ProcessInProgress(matchData);
                    break;

                case MatchStatus.Complete:
                    ProcessComplete(matchData);
                    break;

                default:
                    break;
            }

            void ProcessInitializing(MatchData matchData)
            {
                Debug.Assert(matchData.Status == MatchStatus.Initializing);

                // Wait for all players to enter the designated arena.
                // The match is cancelled if any of the players abandons the match (disconnects or leaves the arena).
                // The match is cancelled if any of the players takes too long to arrive.

                HashSet<Player> readyPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that are ready.
                HashSet<Player> waitingPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that we are waiting on.
                HashSet<Player> abandonedPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that have abandoned the match (but are still connected).
                HashSet<string> abandonedPlayerNames = _objectPoolManager.NameHashSetPool.Get(); // Players that have abandoned the match.

                try
                {
                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            if (slot.Player is null || slot.HasLeftMatchArena)
                            {
                                // The player abandoned the match (disconnected or left the match arena).
                                abandonedPlayerNames.Add(slot.PlayerName!);

                                if (slot.Player is not null)
                                {
                                    abandonedPlayers.Add(slot.Player);
                                }
                            }
                            else
                            {
                                if (matchData.Arena is null)
                                {
                                    // The arena doesn't exist, so we're still waiting.
                                    waitingPlayers.Add(slot.Player);
                                }
                                else
                                {
                                    if (slot.Player.Arena == matchData.Arena
                                        && slot.Player.TryGetExtraData(_pdKey, out PlayerData? playerData)
                                        && playerData.HasFullyEnteredArena)
                                    {
                                        // The player is in the arena and has fully entered (sent a position packet).
                                        readyPlayers.Add(slot.Player);
                                    }
                                    else
                                    {
                                        // Still waiting for the player to enter the proper arena.
                                        waitingPlayers.Add(slot.Player);
                                    }
                                }
                            }
                        }
                    }

                    if (abandonedPlayerNames.Count > 0)
                    {
                        // At least one player abandoned the match. Cancel the match.

                        // Send chat notifications.
                        HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            notifyPlayers.UnionWith(readyPlayers);
                            notifyPlayers.UnionWith(waitingPlayers);
                            notifyPlayers.UnionWith(abandonedPlayers);

                            SendMatchCancellationNotification(notifyPlayers, abandonedPlayerNames, abandonedPlayers, "abandoned the game");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                        }

                        EndMatch(matchData, MatchEndReason.Cancelled, null);

                        // Players that did not abandon the match are placed back into their queues and keep their original position in the queues.
                        readyPlayers.UnionWith(waitingPlayers);
                        _matchmakingQueues.UnsetPlayingDueToCancel(readyPlayers);

                        // Players that abandoned the match are penalized with a delay.
                        _matchmakingQueues.UnsetPlayingAfterDelay(abandonedPlayerNames, _abandonStartPenaltyDuration);
                        return;
                    }

                    if (waitingPlayers.Count > 0)
                    {
                        if (DateTime.UtcNow >= matchData.PhaseExpiration)
                        {
                            // The startup phase expired. Cancel the match.

                            // Send chat notifications.
                            HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                            HashSet<string> waitingPlayerNames = _objectPoolManager.NameHashSetPool.Get();
                            try
                            {
                                notifyPlayers.UnionWith(readyPlayers);
                                notifyPlayers.UnionWith(waitingPlayers);

                                foreach (Player player in waitingPlayers)
                                {
                                    waitingPlayerNames.Add(player.Name!);
                                }

                                SendMatchCancellationNotification(notifyPlayers, waitingPlayerNames, null, "took too long to arrive");
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                                _objectPoolManager.NameHashSetPool.Return(waitingPlayerNames);
                            }

                            EndMatch(matchData, MatchEndReason.Cancelled, null);

                            // Players that were ready are placed back into their queues and keep their original position in the queues.
                            _matchmakingQueues.UnsetPlayingDueToCancel(readyPlayers);

                            // Players that we were still waiting on took too long to arrive (downloading the map and lvz files).
                            // They are NOT placed back into the queue, but they are allowed to manually requeue.
                            _matchmakingQueues.UnsetPlaying(waitingPlayers, false);

                            return;
                        }
                        else
                        {
                            // Keep waiting for the remaining players to arrive.
                            return;
                        }
                    }

                    // All players are have arrived!
                    // Burn Items if configured in match settings (overrides item refill)
                    ItemsAction itemsAction = matchData.Configuration.BurnItemsOnSpawn ? ItemsAction.Burn : ItemsAction.Full;

                    // Set freqs and ships, and move players to their starting locations.
                    foreach (Team team in matchData.Teams)
                    {
                        TileCoordinates startLocation = SetTeamStartLoocation(team);

                        foreach (PlayerSlot playerSlot in team.Slots)
                        {
                            ResetSlotForMatchStart(playerSlot);

                            Player? player = playerSlot.Player;
                            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                                continue;

                            // The next phase will check if the player is ready to start.
                            playerData.IsReadyToStart = false;

                            SetShipAndFreq(playerSlot, false, startLocation, itemsAction);
                        }
                    }

                    // Start the next phase.
                    matchData.Status = MatchStatus.StartingCheck;
                    matchData.PhaseExpiration = DateTime.UtcNow + matchData.Configuration.ReadyWaitDuration;

                    // Notify players to ready up.
                    _chat.SendSetMessage(readyPlayers, $"You have 15 seconds to indicate that you are READY by rotating your ship or by firing a weapon.");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(readyPlayers);
                    _objectPoolManager.PlayerSetPool.Return(waitingPlayers);
                    _objectPoolManager.PlayerSetPool.Return(abandonedPlayers);
                    _objectPoolManager.NameHashSetPool.Return(abandonedPlayerNames);
                }
            }

            void ProcessStartingCheck(MatchData matchData)
            {
                Debug.Assert(matchData.Status == MatchStatus.StartingCheck);

                // Wait for all players to indicate they are ready.
                // The match is cancelled if any of the players abandons the match (disconnects, leaves the arena, or changes to spec).
                // The match is cancelled if any of the players takes too long to ready up.

                HashSet<Player> readyPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that are ready.
                HashSet<Player> waitingPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that we are waiting on.
                HashSet<Player> abandonedPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that have abandoned the match (but are still connected).
                HashSet<string> abandonedPlayerNames = _objectPoolManager.NameHashSetPool.Get(); // Players that have abandoned the match.

                try
                {
                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            if (slot.Player is null || slot.HasLeftMatchArena || slot.Player.Ship == ShipType.Spec)
                            {
                                // The player abandoned the match (disconnected, left the match arena, or changed to spectator mode).
                                abandonedPlayerNames.Add(slot.PlayerName!);

                                if (slot.Player is not null)
                                {
                                    abandonedPlayers.Add(slot.Player);
                                }
                            }
                            else
                            {
                                if (slot.Player.TryGetExtraData(_pdKey, out PlayerData? playerData)
                                    && playerData.IsReadyToStart)
                                {
                                    // The player has indicated that they are ready.
                                    readyPlayers.Add(slot.Player);
                                }
                                else
                                {
                                    // Still waiting for the player to indicate that they are ready.
                                    waitingPlayers.Add(slot.Player);
                                }
                            }
                        }
                    }

                    if (abandonedPlayerNames.Count > 0)
                    {
                        // At least one player abandoned the match. Cancel the match.

                        // Send chat notifications.
                        HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            notifyPlayers.UnionWith(readyPlayers);
                            notifyPlayers.UnionWith(waitingPlayers);
                            notifyPlayers.UnionWith(abandonedPlayers);

                            SendMatchCancellationNotification(notifyPlayers, abandonedPlayerNames, abandonedPlayers, "abandoned the game");

                            // Additionally, send all players to spec.
                            foreach (Player player in notifyPlayers)
                            {
                                _game.SetShipAndFreq(player, ShipType.Spec, matchData.Arena!.SpecFreq);
                            }
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                        }

                        EndMatch(matchData, MatchEndReason.Cancelled, null);

                        // Players that did not abandon the match are placed back into their queues and keep their original position in the queues.
                        readyPlayers.UnionWith(waitingPlayers);
                        _matchmakingQueues.UnsetPlayingDueToCancel(readyPlayers);

                        // Players that abandoned the match are penalized with a delay.
                        _matchmakingQueues.UnsetPlayingAfterDelay(abandonedPlayerNames, _abandonStartPenaltyDuration);

                        return;
                    }

                    if (waitingPlayers.Count > 0)
                    {
                        if (DateTime.UtcNow >= matchData.PhaseExpiration)
                        {
                            // The startup phase expired. Cancel the match.

                            // Send chat notifications.
                            HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                            HashSet<string> waitingPlayerNames = _objectPoolManager.NameHashSetPool.Get();
                            try
                            {
                                notifyPlayers.UnionWith(readyPlayers);
                                notifyPlayers.UnionWith(waitingPlayers);

                                foreach (Player player in waitingPlayers)
                                {
                                    waitingPlayerNames.Add(player.Name!);
                                }

                                SendMatchCancellationNotification(notifyPlayers, waitingPlayerNames, waitingPlayers, "did not ready up");

                                // Additionally, send all players to spec.
                                foreach (Player player in notifyPlayers)
                                {
                                    _game.SetShipAndFreq(player, ShipType.Spec, matchData.Arena!.SpecFreq);
                                }
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                                _objectPoolManager.NameHashSetPool.Return(waitingPlayerNames);
                            }

                            EndMatch(matchData, MatchEndReason.Cancelled, null);

                            // Players that were ready are placed back into their queues and keep their original position in the queues.
                            _matchmakingQueues.UnsetPlayingDueToCancel(readyPlayers);

                            // Players that we were still waiting for took too long to ready up.
                            // They are penalized with a delay to discourage from attempting to cherry pick their matches (didn't like their teammates) or trolling.
                            foreach (Player player in waitingPlayers)
                            {
                                abandonedPlayerNames.Add(player.Name!);
                            }

                            _matchmakingQueues.UnsetPlayingAfterDelay(abandonedPlayerNames, _notReadyStartPenaltyDuration);

                            return;
                        }
                        else
                        {
                            // Keep waiting for the remaining players to indicate they're ready.
                            return;
                        }
                    }

                    // All players are ready!

                    // Reset ships if match settings dont specify burned items
                    if (!matchData.Configuration.BurnItemsOnSpawn)
                    {
                        foreach (Player player in readyPlayers)
                        {
                            _game.ShipReset(player);
                        }
                    }

                    // Start the next phase.
                    matchData.Status = MatchStatus.StartingCountdown;
                    matchData.PhaseExpiration = null;
                    matchData.StartCountdown = (int)matchData.Configuration.StartCountdownDuration.TotalSeconds;

                    SendMatchStartingNotification(matchData);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(readyPlayers);
                    _objectPoolManager.PlayerSetPool.Return(waitingPlayers);
                    _objectPoolManager.PlayerSetPool.Return(abandonedPlayers);
                    _objectPoolManager.NameHashSetPool.Return(abandonedPlayerNames);
                }

                void SendMatchStartingNotification(MatchData matchData)
                {
                    HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        GetPlayersToNotify(matchData, notifyPlayers);

                        // Match details
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            
                            sb.Append($"Starting a {matchData.MatchIdentifier.MatchType} match");

                            if (matchData.Configuration.Boxes.Length > 0)
                            {
                                sb.Append($" in box {matchData.MatchIdentifier.BoxIdx}");
                            }

                            sb.Append($"  --  Time Limit: ");

                            if (matchData.Configuration.TimeLimit is null)
                                sb.Append("None");
                            else
                                sb.AppendFriendlyTimeSpan(matchData.Configuration.TimeLimit.Value);

                            sb.Append($"  --  {matchData.Configuration.LivesPerPlayer} lives per slot.");

                            _chat.SendSetMessage(notifyPlayers, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                    }
                }
            }

            async void ProcessStartingCountdown(MatchData matchData)
            {
                Debug.Assert(matchData.Status == MatchStatus.StartingCountdown);

                if (matchData.LeagueGame is null)
                {
                    // The match is cancelled if any of the players abandons the match (disconnects, leaves the arena, or changes to spec).

                    HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get(); // Players that are ready.
                    HashSet<Player> abandonedPlayers = _objectPoolManager.PlayerSetPool.Get(); // Players that have abandoned the match (but are still connected).
                    HashSet<string> abandonedPlayerNames = _objectPoolManager.NameHashSetPool.Get(); // Players that have abandoned the match.

                    try
                    {
                        foreach (Team team in matchData.Teams)
                        {
                            foreach (PlayerSlot slot in team.Slots)
                            {
                                if (slot.Player is null || slot.HasLeftMatchArena || slot.Player.Ship == ShipType.Spec)
                                {
                                    // The player abandoned the match (disconnected, left the match arena, or changed to spectator mode).
                                    abandonedPlayerNames.Add(slot.PlayerName!);

                                    if (slot.Player is not null)
                                    {
                                        abandonedPlayers.Add(slot.Player);
                                    }
                                }
                                else
                                {
                                    players.Add(slot.Player);
                                }
                            }
                        }

                        if (abandonedPlayerNames.Count > 0)
                        {
                            // At least one player abandoned the match. Cancel the match.

                            // Send chat notifications.
                            HashSet<Player> notifyPlayers = _objectPoolManager.PlayerSetPool.Get();
                            try
                            {
                                notifyPlayers.UnionWith(players);
                                notifyPlayers.UnionWith(abandonedPlayers);

                                SendMatchCancellationNotification(notifyPlayers, abandonedPlayerNames, abandonedPlayers, "abandoned the game");

                                // Additionally, send all players to spec.
                                foreach (Player player in notifyPlayers)
                                {
                                    _game.SetShipAndFreq(player, ShipType.Spec, matchData.Arena!.SpecFreq);
                                }
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(notifyPlayers);
                            }

                            EndMatch(matchData, MatchEndReason.Cancelled, null);

                            // Players that did not abandon the match are placed back into their queues and keep their original position in the queues.
                            _matchmakingQueues.UnsetPlayingDueToCancel(players);

                            // Players that abandoned the match are penalized with a delay.
                            _matchmakingQueues.UnsetPlayingAfterDelay(abandonedPlayerNames, _abandonStartPenaltyDuration);

                            return;
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(players);
                        _objectPoolManager.PlayerSetPool.Return(abandonedPlayers);
                        _objectPoolManager.NameHashSetPool.Return(abandonedPlayerNames);
                    }
                }

                //
                // Countdown
                //

                HashSet<Player> notifyCountdownPlayers = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(matchData, notifyCountdownPlayers);

                    if (matchData.StartCountdown > 0)
                    {
                        if (matchData.StartCountdown <= 3)
                        {
                            _chat.SendSetMessage(notifyCountdownPlayers, $"-{matchData.StartCountdown}-");
                        }

                        matchData.StartCountdown--;
                        return;
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifyCountdownPlayers);
                }

                matchData.Status = MatchStatus.Starting;

                if (matchData.LeagueGame is not null)
                {
                    // For league games, the slots are not assigned until the last moment, which is now.
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena != matchData.Arena || player.Ship == ShipType.Spec)
                            continue;

                        Team? team = null;
                        PlayerSlot? slot = null;

                        foreach (Team otherTeam in matchData.Teams)
                        {
                            if (otherTeam.Freq == player.Freq)
                            {
                                team = otherTeam;
                                break;
                            }
                        }

                        if (team is not null)
                        {
                            foreach (PlayerSlot otherSlot in team.Slots)
                            {
                                if (string.IsNullOrWhiteSpace(otherSlot.PlayerName))
                                {
                                    slot = otherSlot;
                                    break;
                                }
                            }
                        }

                        if (slot is null)
                        {
                            // Player is not in the match. Change the player to spectator mode.
                            _game.SetShip(player, ShipType.Spec);
                        }
                        else
                        {
                            // Assign the slot.
                            AssignSlot(slot, player);
                            slot.Status = PlayerSlotStatus.Playing;

                            matchData.ParticipationList.Add(new PlayerParticipationRecord(player.Name!, false, false));
                        }
                    }

                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            if (slot.Status == PlayerSlotStatus.None && matchData.Configuration.AllowFillUnusedSlots)
                                slot.Status = PlayerSlotStatus.Waiting;

                            ResetSlotForMatchStart(slot);
                        }
                    }

                    // The initial players in the match can't change now.
                    // Tell the stats module to initialize. It may get info about the players (e.g. current ratings).
                    if (_teamVersusStatsBehavior is not null)
                    {
                        await _teamVersusStatsBehavior.InitializeAsync(matchData);
                    }

                    MatchStartingCallback.Fire(_broker, matchData);
                }

                // Start the match.
                matchData.Status = MatchStatus.InProgress;
                matchData.Started = DateTime.UtcNow;

                if (matchData.Configuration.TimeLimit is not null)
                {
                    matchData.PhaseExpiration = matchData.Started + matchData.Configuration.TimeLimit.Value;
                }
                else
                {
                    matchData.PhaseExpiration = null;
                }

                // Send the GO notification.
                // We can't assume players are still valid after an await, so get the players from scratch.
                HashSet<Player> notifyGoPlayers = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(matchData, notifyGoPlayers);

                    _chat.SendSetMessage(notifyGoPlayers, ChatSound.Ding, "GO!");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifyGoPlayers);
                }

                // Tell the stats module that the match has started.
                if (_teamVersusStatsBehavior is not null)
                {
                    await _teamVersusStatsBehavior.MatchStartedAsync(matchData);
                }

                MatchStartedCallback.Fire(_broker, matchData);
                TeamVersusMatchStartedCallback.Fire(matchData.Arena!, matchData);
            }

            void ProcessInProgress(MatchData matchData)
            {
                Debug.Assert(matchData.Status == MatchStatus.InProgress);

                if (matchData.PhaseExpiration is null)
                    return;

                // TODO: add time limit notifications (e.g. when 5 minutes remain)

                if (DateTime.UtcNow >= matchData.PhaseExpiration)
                {
                    // Check for a winner due to hitting the time limit.
                    if (TryGetTimeLimitWinner(matchData, out Team? winnerTeam))
                    {
                        EndMatch(matchData, MatchEndReason.Decided, winnerTeam);
                        return;
                    }

                    // No winner yet. Check if it can be moved to overtime.
                    if (!matchData.IsOvertime && matchData.Configuration.OverTimeLimit is not null)
                    {
                        matchData.IsOvertime = true;
                        matchData.PhaseExpiration += matchData.Configuration.OverTimeLimit.Value;

                        // Notify about overtime starting
                        HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            GetPlayersToNotify(matchData, players);

                            sb.Append($"Overtime starting -- Time limit: ");
                            sb.AppendFriendlyTimeSpan(matchData.Configuration.OverTimeLimit.Value);

                            _chat.SendSetMessage(players, sb);
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(players);
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }

                        return;
                    }

                    // If the match hasn't ended by now, it's a draw.
                    EndMatch(matchData, MatchEndReason.Draw, null);
                }
            }

            void ProcessComplete(MatchData matchData)
            {
                Debug.Assert(matchData.Status == MatchStatus.Complete);

                if (matchData.PhaseExpiration is not null && (DateTime.UtcNow < matchData.PhaseExpiration))
                    return;

                // TODO: a short 5 - 10 second wait period between matches

                //matchData.Status = MatchStatus.None;
            }
        }

        private static bool CanShipChangeNow(Player player, PlayerData playerData)
        {
            PlayerSlot? playerSlot = playerData.AssignedSlot;

            return playerSlot is null
                || (player.Arena is not null // player is in an arena
                    && player.Arena == playerSlot.MatchData.Arena // player is in the match's arena
                    && player.Freq == playerSlot.Team.Freq // player is on the team's freq
                    && player.Ship != ShipType.Spec // player is in a ship
                    && (IsStartingPhase(playerSlot.MatchData.Status)
                        || (playerSlot.MatchData.Status == MatchStatus.InProgress
                            && (playerSlot.AllowShipChangeExpiration is not null && playerSlot.AllowShipChangeExpiration.Value > DateTime.UtcNow)
                        )
                    ) // is within a period that ship changes are allowed (e.g. starting phase or after a death))
                );
        }

        private void SetNextShip(Player player, PlayerData playerData, ShipType ship, bool notify)
        {
            if (ship == ShipType.Spec)
                return;

            playerData.NextShip = ship;

            if (notify)
            {
                bool isForCurrentMatch = playerData.AssignedSlot is not null && playerData.AssignedSlot.Lives > 1;

                if (isForCurrentMatch)
                {
                    _chat.SendMessage(player, $"Your next ship will be a {ship}.");
                }
                else
                {
                    _chat.SendMessage(player, $"Your next ship will be a {ship} for your next match.");
                }
            }
        }

        private void SetShipAndFreq(PlayerSlot slot, bool isRefill, TileCoordinates? startLocation, ItemsAction itemsAction)
        {
            Player? player = slot.Player;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            // Override spawn settings if needed.
            SendSpawnOverrides(player, playerData, slot);

            ShipType ship;
            if (isRefill)
            {
                ship = slot.Ship;

                if (ship == ShipType.Spec)
                {
                    // The ship should not be spectator mode when the slot is being refilled, but handle it just to be safe.
                    ship = ShipType.Warbird;
                }

                playerData.NextShip ??= ship;
            }
            else
            {
                if (playerData.NextShip is not null)
                    ship = playerData.NextShip.Value;
                else
                    playerData.NextShip = ship = ShipType.Warbird;
            }

            slot.Status = PlayerSlotStatus.Playing;
            slot.InactiveTimestamp = null;

            // Set what to do about items when the ship is changed.
            slot.ShipChangeItemsAction = itemsAction;

            // Set the ship and freq (this is automatically in the center).
            // NOTE: This fires the PreShipFreqChangeCallback which adjusts items accordingly.
            _game.SetShipAndFreq(player, ship, slot.Team.Freq);

            // Warp the player to the starting location.
            if (startLocation is not null)
            {
                _game.WarpTo(player, startLocation.Value.X, startLocation.Value.Y);
            }

            // Remove any existing timers for the slot.
            _mainloopTimer.ClearTimer<PlayerSlot>(MainloopTimer_ProcessInactiveSlot, slot);

            void SendSpawnOverrides(Player player, PlayerData playerData, PlayerSlot slot)
            {
                Arena? arena = player.Arena;
                if (arena is null)
                    return;

                MatchData matchData = slot.MatchData;
                bool changed = false;

                // Remove any existing overrides.
                changed = UnoverrideSpawnSettings(player, playerData);

                MatchBoxConfiguration boxConfiguration = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx];

                // Add overrides if there are any configured.
                for (int i = 0; i < 4; i++)
                {
                    ref SpawnPosition? spawnPosition = ref boxConfiguration.SpawnPositions[i];
                    if (spawnPosition is null)
                        break;

                    _clientSettings.OverrideSetting(player, _spawnXClientSettingIds[i], spawnPosition.Value.X);
                    _clientSettings.OverrideSetting(player, _spawnYClientSettingIds[i], spawnPosition.Value.Y);
                    _clientSettings.OverrideSetting(player, _spawnRadiusClientSettingIds[i], spawnPosition.Value.Radius);

                    playerData.IsSpawnOverriden = true;
                    changed = true;
                }

                if (changed)
                {
                    _clientSettings.SendClientSettings(player);
                }
            }
        }

        private void AdjustItems(PlayerSlot slot, ItemsAction action)
        {
            switch (action)
            {
                case ItemsAction.Restore:
                    // Adjust the player's items to the prior remaining amounts.
                    RestoreRemainingItems(slot);
                    break;

                case ItemsAction.Burn:
                    // Adjust the player's items to nothing (to override defaults or for certain conditions).
                    RemoveAllItems(slot);
                    break;

                case ItemsAction.Full:
                default:
                    break;
            }
        }

        private void RestoreRemainingItems(PlayerSlot slot)
        {
            Player? player = slot.Player;
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            ref ShipSettings shipSettings = ref arenaData.ShipSettings[(int)slot.Ship];
            AdjustItem(player, Prize.Burst, shipSettings.InitialBurst, slot.RestoreItems.Bursts);
            AdjustItem(player, Prize.Repel, shipSettings.InitialRepel, slot.RestoreItems.Repels);
            AdjustItem(player, Prize.Thor, shipSettings.InitialThor, slot.RestoreItems.Thors);
            AdjustItem(player, Prize.Brick, shipSettings.InitialBrick, slot.RestoreItems.Bricks);
            AdjustItem(player, Prize.Decoy, shipSettings.InitialDecoy, slot.RestoreItems.Decoys);
            AdjustItem(player, Prize.Rocket, shipSettings.InitialRocket, slot.RestoreItems.Rockets);
            AdjustItem(player, Prize.Portal, shipSettings.InitialPortal, slot.RestoreItems.Portals);

            void AdjustItem(Player player, Prize prize, byte initial, byte remaining)
            {
                short adjustAmount = (short)(initial - remaining);
                if (adjustAmount <= 0)
                    return;

                _game.GivePrize(player, (Prize)(-(short)prize), adjustAmount);
            }
        }

        /// <summary>
        /// Overrides initial shipSetting items
        /// </summary>
        /// <remarks>
        /// This is used to burn ships for various purposes.
        /// </remarks>
        /// <param name="slot">The player slot to remove items for.</param>
        private void RemoveAllItems(PlayerSlot slot)
        {
            Player? player = slot.Player;
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            ref ShipSettings shipSettings = ref arenaData.ShipSettings[(int)slot.Ship];
            AdjustItem(player, Prize.Burst, shipSettings.InitialBurst);
            AdjustItem(player, Prize.Repel, shipSettings.InitialRepel);
            AdjustItem(player, Prize.Thor, shipSettings.InitialThor);
            AdjustItem(player, Prize.Brick, shipSettings.InitialBrick);
            AdjustItem(player, Prize.Decoy, shipSettings.InitialDecoy);
            AdjustItem(player, Prize.Rocket, shipSettings.InitialRocket);
            AdjustItem(player, Prize.Portal, shipSettings.InitialPortal);

            void AdjustItem(Player player, Prize prize, byte initial)
            {
                short adjustAmount = (short)(0 - initial);
                if (adjustAmount >= 0)
                    return;

                _game.GivePrize(player, (Prize)(-(short)prize), adjustAmount);
            }
        }

        /// <summary>
        /// Unoverrides a player's spawn settings.
        /// </summary>
        /// <remarks>
        /// This method does not send any setting changes to the player.
        /// It is the caller's responsibility to call <see cref="IClientSettings.SendClientSettings(Player)"/>
        /// since the caller may be updating other setting overrides as well.
        /// </remarks>
        /// <param name="player">The player to unoverride settings for.</param>
        /// <param name="playerData">The player's extra data.</param>
        /// <returns><see langword="true"/> if an settings override change was made; otherwise, <see langword="false"/>.</returns>
        private bool UnoverrideSpawnSettings(Player player, PlayerData playerData)
        {
            if (!playerData.IsSpawnOverriden)
                return false;

            for (int i = 0; i < 4; i++)
            {
                _clientSettings.UnoverrideSetting(player, _spawnXClientSettingIds[i]);
                _clientSettings.UnoverrideSetting(player, _spawnYClientSettingIds[i]);
                _clientSettings.UnoverrideSetting(player, _spawnRadiusClientSettingIds[i]);
            }

            playerData.IsSpawnOverriden = false;
            return true;
        }

        /// <summary>
        /// Schedules a check for match completion due to the possibility of a win condition.
        /// </summary>
        /// <remarks>
        /// This purposely uses a separate delegate than <see cref="ScheduleCheckForMatchCompletion(MatchData, int)"/> uses.
        /// This is so that if a player dies, it schedules a check.
        /// If another player dies before the check occurs, it overrides the first scheduled check. 
        /// Effectively extending the time till the check is processed.
        /// However, we don't want other (non- win condition) checks for match completion to affect this.
        /// </remarks>
        /// <param name="matchData"></param>
        private void ScheduleWinConditionCheckForMatchCompletion(MatchData matchData)
        {
            _mainloopTimer.ClearTimer<MatchData>(MainloopTimer_WinConditionCheckForMatchCompletion, matchData);
            _mainloopTimer.SetTimer(MainloopTimer_WinConditionCheckForMatchCompletion, (int)matchData.Configuration.WinConditionDelay.TotalMilliseconds, Timeout.Infinite, matchData, matchData);
        }

        private bool MainloopTimer_WinConditionCheckForMatchCompletion(MatchData matchData)
        {
            CheckForMatchCompletion(matchData);
            return false;
        }

        private void ScheduleCheckForMatchCompletion(MatchData matchData, int timerDelayMs)
        {
            _mainloopTimer.ClearTimer<MatchData>(MainlooopTimer_CheckForMatchCompletion, matchData);
            _mainloopTimer.SetTimer(MainlooopTimer_CheckForMatchCompletion, timerDelayMs, Timeout.Infinite, matchData, matchData);
        }

        private bool MainlooopTimer_CheckForMatchCompletion(MatchData matchData)
        {
            CheckForMatchCompletion(matchData);
            return false;
        }

        private void CheckForMatchCompletion(MatchData matchData)
        {
            if (matchData is null)
                return;

            if (matchData.Status != MatchStatus.InProgress)
                return;

            bool isPlayAreaCheckEnabled = !string.IsNullOrWhiteSpace(matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].PlayAreaMapRegion);
            int remainingTeams = 0;
            Team? lastRemainingTeam = null;

            foreach (Team team in matchData.Teams)
            {
                bool isTeamKnockedOut = true;

                foreach (PlayerSlot slot in team.Slots)
                {
                    if (slot.Status == PlayerSlotStatus.Playing
                        && slot.Player is not null
                        && (!isPlayAreaCheckEnabled || slot.IsInPlayArea))
                    {
                        isTeamKnockedOut = false;
                    }
                }

                if (!isTeamKnockedOut)
                {
                    remainingTeams++;
                    lastRemainingTeam = team;
                }
            }

            if (remainingTeams == 0)
            {
                // DRAW
                EndMatch(matchData, MatchEndReason.Draw, null);
            }
            else if (remainingTeams == 1)
            {
                // one team remains and therefore won
                EndMatch(matchData, MatchEndReason.Decided, lastRemainingTeam);
            }
            else if (matchData.IsOvertime)
            {
                if (TryGetTimeLimitWinner(matchData, out Team? winnerTeam))
                {
                    EndMatch(matchData, MatchEndReason.Decided, winnerTeam);
                }
            }
        }

        private static bool TryGetTimeLimitWinner(MatchData matchData, [MaybeNullWhen(false)] out Team winnerTeam)
        {
            Team? top1 = null;
            Team? top2 = null;
            foreach (Team team in matchData.Teams)
            {
                if (top1 is null || team.Score >= top1.Score)
                {
                    top2 = top1;
                    top1 = team;
                }
                else if (top2 is null || team.Score >= top2.Score)
                {
                    top2 = team;
                }
            }

            if (top1 is not null && top2 is not null // neither should be null since there should be at least 2 teams
                && top1.Score >= (top2.Score + matchData.Configuration.TimeLimitWinBy))
            {
                winnerTeam = top1;
                return true;
            }

            winnerTeam = null;
            return false;
        }

        private async void EndMatch(MatchData matchData, MatchEndReason reason, Team? winnerTeam)
        {
            if (matchData is null)
                return;

            if (matchData.Status == MatchStatus.None)
                return;

            if (matchData.Status == MatchStatus.InProgress)
            {
                // Change the status to stop any additional attempts to end the match while we're ending it.
                matchData.Status = MatchStatus.Complete;

                MatchEndingCallback.Fire(_broker, matchData);

                bool isNotificationHandled = false;

                if (_teamVersusStatsBehavior is not null)
                {
                    // Tell the stats module to process the end of a match.
                    // It may, or may not, attempt to save data to a database.
                    // If it tries to save to the database, that work is done asynchronously.
                    // Otherwise, it's done synchronously.
                    // We can't tear down the object model of the match until it's done, so we await it.
                    isNotificationHandled = await _teamVersusStatsBehavior.MatchEndedAsync(matchData, reason, winnerTeam);
                }

                if (!isNotificationHandled)
                {
                    // Send a basic notification that the match ended.
                    HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();
                    StringBuilder scoreBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        // Notification to players that have any association to the match, regardless of the arena they are in.
                        _matchFocus.TryGetPlayers(matchData, notifySet, MatchFocusReasons.Any, null);

                        foreach (Team team in matchData.Teams)
                        {
                            if (scoreBuilder.Length > 0)
                                scoreBuilder.Append('-');

                            scoreBuilder.Append(team.Score);
                        }

                        switch (reason)
                        {
                            case MatchEndReason.Decided:
                                if (winnerTeam is not null)
                                {
                                    scoreBuilder.Append($" Freq {winnerTeam.Freq}");
                                }
                                break;

                            case MatchEndReason.Draw:
                                scoreBuilder.Append(" DRAW");
                                break;

                            case MatchEndReason.Cancelled:
                                scoreBuilder.Append(" CANCELLED");
                                break;
                        }

                        _chat.SendSetMessage(notifySet, $"Final {matchData.MatchIdentifier.MatchType} Score: {scoreBuilder}");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(notifySet);
                        _objectPoolManager.StringBuilderPool.Return(scoreBuilder);
                    }
                }

                Arena? arena = _arenaManager.FindArena(matchData.ArenaName); // Note: this can be null (the arena can be destroyed before the match ends)
                ArenaData? arenaData = null;
                if (arena is not null)
                    _arenaDataDictionary.TryGetValue(arena, out arenaData);

                MatchEndedCallback.Fire(_broker, matchData);
                TeamVersusMatchEndedCallback.Fire(arena ?? _broker, matchData, reason, winnerTeam);

                if (matchData.LeagueGame is not null)
                {
                    LeagueMatchEndedCallback.Fire(_broker, matchData);

                    if (arenaData is not null && arenaData.LeagueMatch == matchData)
                    {
                        arenaData.LeagueMatch = null;
                    }
                }

                if (arena is not null 
                    && arenaData is not null 
                    && !string.IsNullOrWhiteSpace(arenaData.ReplayRecordingFilePath))
                {
                    StopRecordingReplay(arena, arenaData);
                }

                foreach (Team team in matchData.Teams)
                {
                    foreach (PlayerSlot slot in team.Slots)
                    {
                        if (slot.SubPlayer is not null)
                        {
                            CancelSubInProgress(slot, true);
                        }

                        if (slot.Player is not null
                            && arena is not null
                            && slot.Player.Arena == arena
                            && (slot.Player.Ship != ShipType.Spec || slot.Player.Freq != arena.SpecFreq))
                        {
                            // Spec any remaining players
                            _game.SetShipAndFreq(slot.Player, ShipType.Spec, arena.SpecFreq);
                        }

                        UnassignSlot(slot);
                    }
                }

                //
                // Clear the 'Playing' state of all players that were associated with the now completed match.
                //

                // Note: since the order matters, this uses an array.
                string[] playerNames = ArrayPool<string>.Shared.Rent(matchData.ParticipationList.Count);
                try
                {
                    int playerNameIndex = 0;

                    // Unset the players that are allowed to automatically requeue.
                    foreach (PlayerParticipationRecord record in matchData.ParticipationList)
                    {
                        if (!record.LeftWithoutSub)
                        {
                            playerNames[playerNameIndex++] = record.PlayerName;
                        }
                    }

                    if (playerNameIndex > 0)
                    {
                        _matchmakingQueues.UnsetPlayingByName(new ArraySegment<string>(playerNames, 0, playerNameIndex), true);
                        playerNameIndex = 0;
                    }

                    // Unset the players that are not allowed to automatically requeue.
                    foreach (PlayerParticipationRecord record in matchData.ParticipationList)
                    {
                        if (record.LeftWithoutSub)
                        {
                            playerNames[playerNameIndex++] = record.PlayerName;
                        }
                    }

                    if (playerNameIndex > 0)
                    {
                        _matchmakingQueues.UnsetPlayingByName(new ArraySegment<string>(playerNames, 0, playerNameIndex), false);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(playerNames, true);
                }
            }
            else
            {
                if (_teamVersusStatsBehavior is not null)
                {
                    await _teamVersusStatsBehavior.MatchEndedAsync(matchData, reason, winnerTeam);
                }
            }

            //
            // Clear match data.
            //

            foreach (PlayerParticipationRecord record in matchData.ParticipationList)
            {
                _playerSlotDictionary.Remove(record.PlayerName);
            }

            matchData.ParticipationList.Clear();

            if (matchData.LeagueGame is not null)
            {
                // Do not reuse MatchData for league games.
                _matchDataDictionary.Remove(matchData.MatchIdentifier);
                return;
            }

            // Reset the entire match, including all the teams slots.
            // This also sets the status to None, which makes it available to host another match.
            matchData.Reset();

            if (!_matchConfigurationDictionary.TryGetValue(matchData.MatchIdentifier.MatchType, out MatchConfiguration? configuration)
                || configuration != matchData.Configuration)
            {
                // The configuration has changed.
                // Discard the MatchData. This will allow a new one to be created with the new configuration.
                _matchDataDictionary.Remove(matchData.MatchIdentifier);

                return;
            }

            string? queueName = matchData.Configuration.QueueName;
            if (queueName is not null)
            {
                // Now that the match has ended, check if there are enough players available to refill it.
                _mainloop.QueueMainWorkItem(MainloopWork_MakeMatch, queueName);
            }

            // local helper for a mainloop work item that attempts to make a match
            void MainloopWork_MakeMatch(string queueName)
            {
                if (_queueDictionary.TryGetValue(queueName, out TeamVersusMatchmakingQueue? queue))
                {
                    MakeMatch(queue);
                }
            }
        }

        private static bool IsStartingPhase(MatchStatus status)
        {
            return status == MatchStatus.Initializing || status == MatchStatus.StartingCheck || status == MatchStatus.StartingCountdown;
        }

        private static bool IsPublicPlayFreq(short freq)
        {
            return freq >= 0 && freq <= 9;
        }

        private static string GetItemsActionDescription(ItemsAction action)
        {
            return action switch
            {
                ItemsAction.Full => "Full Ship",
                ItemsAction.Burn => "Items Burned",
                ItemsAction.Restore => "Items Restored",
                _ => string.Empty,
            };
        }

        #region Helper types

        /// <summary>
        /// Options for the ?items command.
        /// </summary>
        private enum ItemsCommandOption
        {
            /// <summary>
            /// The command is disabled.
            /// </summary>
            None = 0,

            /// <summary>
            /// Print out the # of repels and # of rockets that a player has.
            /// </summary>
            RepelsAndRockets,
        }

        /// <summary>
        /// How items are handled for a player that subs into a match or ship changes in the allowed window (after death, after sub in; and without engaging).
        /// </summary>
        private enum ItemsAction
        {
            /// <summary>
            /// The player that subs in gets a full ship.
            /// </summary>
            Full = 0,

            /// <summary>
            /// The player that subs in gets no items.
            /// </summary>
            Burn,

            /// <summary>
            /// The player that subs in gets the items restored based on what the previous player had remaining.
            /// </summary>
            Restore,
        }

        private class MatchConfiguration : IMatchConfiguration
        {
            public required string MatchType { get; init; }
            public required long? GameTypeId { get; init; }
            public required long? LeagueId { get; init; }
            public required string? QueueName { get; init; }
            public required string ArenaBaseName { get; init; }
            public required int MaxArenas { get; init; }
            public required int NumTeams { get; init; }
            public required int PlayersPerTeam { get; init; }
            public required int LivesPerPlayer { get; init; }
            public required TimeSpan ArrivalWaitDuration { get; init; }
            public required TimeSpan ReadyWaitDuration { get; init; }
            public required TimeSpan StartCountdownDuration { get; init; }
            public required TimeSpan? TimeLimit { get; init; }
            public required TimeSpan? OverTimeLimit { get; init; }
            public required TimeSpan WinConditionDelay { get; init; }
            public required TimeSpan InactiveTeamsMatchCompletionDelay { get; init; }
            public required int TimeLimitWinBy { get; init; }
            public required int MaxLagOuts { get; init; }
            public required TimeSpan AllowShipChangeAfterDeathDuration { get; init; }
            public required TimeSpan AllowShipChangeAfterSubDuration { get; init; }
            public required int MaxShipChangesPerEligibleEvent { get; init; }
            public required TimeSpan InactiveSlotAvailableDelay { get; init; }
            public required TimeSpan DeathSubDuration { get; init; }
            public required int InitialFullSubs { get; init; }
            public required bool SubInMustUsePriorShip { get; init; }
            public required ItemsAction SubInItemsAction { get; init; }
            public required ItemsAction ReturnToMatchItemsAction { get; init; }
            public ItemsCommandOption ItemsCommandOption { get; init; } = ItemsCommandOption.None;
            public required bool BurnItemsOnSpawn { get; init; }
            public required bool AllowFillUnusedSlots { get; init; }
            public required string? ReplayRecordPath { get; init; }

            public required MatchBoxConfiguration[] Boxes;

            ReadOnlySpan<IMatchBoxConfiguration> IMatchConfiguration.Boxes => Boxes;
        }

        private class MatchBoxConfiguration : IMatchBoxConfiguration
        {
            /// <summary>
            /// Available starting locations for each team.
            /// </summary>
            public required TileCoordinates[][] TeamStartLocations;

            public string? PlayAreaMapRegion { get; init; }

            /// <summary>
            /// Spawn position overrides
            /// </summary>
            /// <remarks>
            /// When set, these can override the arena.conf [Spawn] settings.
            /// </remarks>
            public readonly SpawnPosition?[] SpawnPositions = new SpawnPosition?[4];
        }

        /// <summary>
        /// Represents the state / phase of a match.
        /// </summary>
        private enum MatchStatus
        {
            /// <summary>
            /// The match is not currently in use.
            /// In this state, the box can be reserved. 
            /// In all other states, the box is reserved and in use.
            /// </summary>
            None,

            /// <summary>
            /// Gathering players into the proper arena.
            /// 
            /// <para>
            /// For regular matches:
            /// Wait for every player to enter the designated arena and send a position packet (finished map and lvz download).
            /// If any player takes too long, leaves the arena, or disconnects, cancel the match and send the remaining players back to the front of the queue.
            /// </para>
            /// 
            /// <para>
            /// For league matches:
            /// Allow a certain amount of time for players to arrive and join their assigned freqs.
            /// </para>
            /// </summary>
            Initializing,

            /// <summary>
            /// Players have been gathered. They are placed onto their designated teams and into ships.
            /// Each player must indicate they are ready (not AFK) by rotating their ship or by firing a weapon.
            /// If any player takes too long, switches to spec, leaves the arena, or disconnects, cancel the match and send the remaining players back to the front of the queue.
            /// </summary>
            StartingCheck,

            /// <summary>
            /// All players have confirmed they are ready.
            /// A starting countdown will be printed (optional).
            /// If any player switches to spec, leaves the arena, or disconnects, cancel the match and send the remaining players back to the front of the queue.
            /// When the countdown is over, the match will be started.
            /// </summary>
            StartingCountdown,

            /// <summary>
            /// The match is starting.
            /// The initial players in the match cannot change now.
            /// </summary>
            Starting,

            /// <summary>
            /// The match is ongoing.
            /// </summary>
            InProgress,

            /// <summary>
            /// The match is complete. A bit of time will be given before the next match.
            /// </summary>
            Complete,
        }

        private class MatchData : IMatchData, ILeagueMatch
        {
            private MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration)
            {
                MatchIdentifier = matchIdentifier;
                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ArenaName = Arena.CreateArenaName(Configuration.ArenaBaseName, MatchIdentifier.ArenaNumber);
                Status = MatchStatus.None;
                Teams = new Team[Configuration.NumTeams];
                _readOnlyTeams = new ReadOnlyCollection<ITeam>(Teams);
                ParticipationList = new(Configuration.NumTeams * Configuration.PlayersPerTeam * 2);
            }

            public MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration, short startingFreq) : this(matchIdentifier, configuration)
            {
                for (int teamIdx = 0; teamIdx < Configuration.NumTeams; teamIdx++)
                {
                    Teams[teamIdx] = new Team(this, teamIdx, startingFreq++);
                }
            }

            public MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration, LeagueGameInfo leagueGame) : this(matchIdentifier, configuration)
            {
                if (configuration.NumTeams != leagueGame.Teams.Count)
                    throw new Exception($"# of teams mismatch in the match configuration and league game info. (config: {configuration.NumTeams}, league: {leagueGame.Teams.Count}");

                LeagueGame = leagueGame;

                int teamIdx = 0;
                foreach ((short freq, LeagueTeamInfo leagueTeam) in leagueGame.Teams)
                {
                    Teams[teamIdx] = new Team(this, teamIdx, freq, leagueTeam);
                    teamIdx++;
                }
            }

            public MatchIdentifier MatchIdentifier { get; }

            /// <inheritdoc cref="IMatchData.Configuration"/>
            public readonly MatchConfiguration Configuration;
            IMatchConfiguration IMatchData.Configuration => Configuration;

            public string ArenaName { get; }

            public Arena? Arena { get; set; }

            /// <summary>
            /// The status of the match.
            /// Used to tell if the match is in use (reserved for a game), and if so, what stage it's in.
            /// </summary>
            public MatchStatus Status;

            /// <summary>
            /// When the current phase of the match ends.
            /// The meaning depends on the current <see cref="Status"/>.
            /// <list type="table">
            /// <listheader>
            /// <term>Status</term>
            /// <description>Meaning</description>
            /// </listheader>
            /// <item>
            /// <term><see cref="MatchStatus.Initializing"/></term>
            /// <description>When to stop waiting for players to arrive in the designated arena for the match.</description>
            /// </item>
            /// <item>
            /// <term><see cref="MatchStatus.StartingCheck"/></term>
            /// <description>When to stop waiting for players to indicate that they are ready.</description>
            /// </item>
            /// <item>
            /// <term><see cref="MatchStatus.InProgress"/></term>
            /// <description>When to end the match due to the game time limit.</description>
            /// </item>
            /// </list>
            /// </summary>
            public DateTime? PhaseExpiration;

            /// <summary>
            /// Approximately how many seconds before the match starts.
            /// </summary>
            public int StartCountdown;

            /// <inheritdoc cref="IMatchData.Teams"/>
            public readonly Team[] Teams;
            private readonly ReadOnlyCollection<ITeam> _readOnlyTeams;
            ReadOnlyCollection<ITeam> IMatchData.Teams => _readOnlyTeams;

            public DateTime? Started { get; set; }

            /// <summary>
            /// Whether the match is in overtime.
            /// </summary>
            public bool IsOvertime;

            /// <summary>
            /// List for keeping track of the players that have participated in the match so that they can be unset from the 'Playing' state when the match ends.
            /// </summary>
            /// <remarks>
            /// The order in this list matters. It starts in the order of players taken from the queue.
            /// If players leave mid-match, they are moved to the end.
            /// When the match is completed, the players are unset from their 'playing' state in the proper order.
            /// This allows players that played through the entire match to get re-queued, in the order that they were originally queued in.
            /// </remarks>
            public readonly List<PlayerParticipationRecord> ParticipationList;

            public LeagueGameInfo? LeagueGame { get; }

            public long? LeagueSeasonGameId => LeagueGame?.SeasonGameId;

            long ILeagueMatch.SeasonGameId => LeagueGame?.SeasonGameId ?? throw new InvalidOperationException("Not a league match.");

            public bool IsForcedStart { get; set; } = false;

            public void Reset()
            {
                Status = MatchStatus.None;
                PhaseExpiration = null;
                StartCountdown = 0;
                Started = null;
                IsOvertime = false;
                ParticipationList.Clear();
                IsForcedStart = false;

                foreach (Team team in Teams)
                {
                    team.Reset();
                }
            }
        }

        /// <summary>
        /// Record to keep track of a player's participation in a match.
        /// </summary>
        /// <param name="PlayerName">The name of the player.</param>
        /// <param name="WasSubIn">Whether the player entered the match as a sub-in.</param>
        /// <param name="LeftWithoutSub">Whether the player left the match without having a replacement player ready sub-in.</param>
        private record struct PlayerParticipationRecord(string PlayerName, bool WasSubIn, bool LeftWithoutSub);

        private class Team : ITeam
        {
            public Team(MatchData matchData, int teamIdx, short freq)
            {
                MatchData = matchData;
                TeamIdx = teamIdx;
                Freq = freq;
                Slots = new PlayerSlot[matchData.Configuration.PlayersPerTeam];
                _readOnlySlots = new ReadOnlyCollection<IPlayerSlot>(Slots);

                for (int slotIdx = 0; slotIdx < Slots.Length; slotIdx++)
                {
                    Slots[slotIdx] = new PlayerSlot(this, slotIdx);
                }

                AllowedToSubLookup = AllowedToSub.GetAlternateLookup<ReadOnlySpan<char>>();

                Reset();
            }

            public Team(MatchData matchData, int teamIdx, short freq, LeagueTeamInfo leagueTeam) : this(matchData, teamIdx, freq)
            {
                LeagueTeam = leagueTeam;
            }

            /// <inheritdoc cref="ITeam.MatchData"/>
            public readonly MatchData MatchData;
            IMatchData ITeam.MatchData => MatchData;

            public int TeamIdx { get; }

            public short Freq { get; }

            /// <inheritdoc cref="ITeam.Slots"/>
            public readonly PlayerSlot[] Slots;
            private readonly ReadOnlyCollection<IPlayerSlot> _readOnlySlots;
            ReadOnlyCollection<IPlayerSlot> ITeam.Slots => _readOnlySlots;

            public short Score { get; set; }

            public int StartLocationIdx;

            #region League

            /// <summary>
            /// Info about the league team.
            /// </summary>
            public LeagueTeamInfo? LeagueTeam { get; }

            /// <summary>
            /// The player that is currently holding captain powers of the freq.
            /// </summary>
            public Player? Captain { get; set; }

            /// <summary>
            /// Whether the captain has indicated the team is ready.
            /// </summary>
            public bool IsReady { get; set; }

            /// <summary>
            /// How many full subs the team has available.
            /// </summary>
            public int RemainingFullSubs { get; set; }

            /// <summary>
            /// Members on the roster that have been flagged as not being allowed to play as a starter, using ?allowplay
            /// </summary>
            /// <remarks>Key: player name</remarks>
            public readonly HashSet<string> NonStarter = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Members on the roster that have been flagged as being allowed to ?sub into the game, using ?allowsub
            /// </summary>
            /// <remarks>Key: player name</remarks>
            public readonly HashSet<string> AllowedToSub = new(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> AllowedToSubLookup;

            #endregion

            public void Reset()
            {
                Score = 0;
                StartLocationIdx = 0;
                Captain = null;
                IsReady = false;
                RemainingFullSubs = MatchData.Configuration.InitialFullSubs;
                NonStarter.Clear();
                AllowedToSub.Clear();

                foreach (PlayerSlot slot in Slots)
                {
                    slot.Reset();
                }
            }
        }

        private enum PlayerSlotStatus
        {
            /// <summary>
            /// The slot is unused and should not be filled.
            /// </summary>
            None,

            /// <summary>
            /// The slot is waiting to be filled or refilled.
            /// </summary>
            Waiting,

            /// <summary>
            /// The slot is filled.
            /// </summary>
            Playing,

            /// <summary>
            /// The slot was defeated.
            /// </summary>
            KnockedOut,
        }

        /// <summary>
        /// The reason why a player is allowed a limited time to ship change.
        /// </summary>
        private enum AllowShipChangeReason
        {
            /// <summary>
            /// The player was assigned a slot that was unused.
            /// </summary>
            FillUnused,

            /// <summary>
            /// The player respawned after taking a death.
            /// </summary>
            Death,

            /// <summary>
            /// The player subbed into a slot.
            /// </summary>
            Sub,
        }

        /// <summary>
        /// A slot for a player on a team.
        /// </summary>
        /// <remarks>
        /// For matchmaking matches, slots are assigned to a player immediately upon initialization since the players in the match are known in advance.
        /// For league matches, slots are not assigned until the match actually starts.
        /// </remarks>
        private class PlayerSlot : IPlayerSlot
        {
            /// <inheritdoc cref="IPlayerSlot.MatchData"/>
            public MatchData MatchData => Team.MatchData;
            IMatchData IPlayerSlot.MatchData => MatchData;

            /// <inheritdoc cref="IPlayerSlot.Team"/>
            public readonly Team Team;
            ITeam IPlayerSlot.Team => Team;

            public int SlotIdx { get; }

            /// <summary>
            /// The slot's status.
            /// Used to tell whether the slot needs to be filled with a player.
            /// and whether the slot is knocked out (when checking for a win condition).
            /// </summary>
            public PlayerSlotStatus Status;

            public string? PlayerName { get; set; }

            public Player? Player { get; set; }

            public int? PremadeGroupId { get; set; }

            /// <summary>
            /// For keeping track of whether the initial player assigned to the slot left the arena during match initialization/startup, 
            /// </summary>
            public bool HasLeftMatchArena = false;

            /// <summary>
            /// The time the slot became inactive (player changed to spec or left the arena).
            /// This will allow us to figure out when the slot can be given to a substitute player.
            /// </summary>
            public DateTime? InactiveTimestamp;

            public int LagOuts { get; set; }

            /// <summary>
            /// Node to use for linking into <see cref="_availableSubSlots"/>.
            /// This is so that we don't have to allocate a node, and don't need to maintain a pool of nodes objects.
            /// </summary>
            public readonly LinkedListNode<PlayerSlot> AvailableSubSlotNode;

            /// <summary>
            /// Whether the player has requested to be subbed out.
            /// </summary>
            public bool IsSubRequested;

            /// <summary>
            /// Whether another player can be subbed in to the slot.
            /// </summary>
            public bool CanBeSubbed
            {
                get
                {
                    if (Team.MatchData.LeagueGame is not null)
                    {
                        // League
                        return IsSubRequested || Status == PlayerSlotStatus.Waiting;
                    }
                    else
                    {
                        // Matchmaking
                        return IsSubRequested
                            || (Status == PlayerSlotStatus.Waiting
                                && (LagOuts >= MatchData.Configuration.MaxLagOuts
                                    || (InactiveTimestamp is not null
                                        && (DateTime.UtcNow - InactiveTimestamp.Value) >= MatchData.Configuration.InactiveSlotAvailableDelay
                                    )
                                )
                            );
                    }
                }
            }

            /// <summary>
            /// The player that is in the process of being subbed into the slot.
            /// </summary>
            public Player? SubPlayer;

            /// <summary>
            /// Whether the <see cref="Player"/> is at full energy.
            /// This is used when trying to sub a player in.
            /// </summary>
            /// <remarks>
            /// This field is used instead of Player.Position.Energy since Player.Position.Energy might not be up to date 
            /// depending on the order that position packet handlers are executed. That is, when our position packet handler 
            /// looks at Player.Position.Energy, the value may not have been updated yet. Instead, our position packet handler 
            /// will set this flag, and then call the logic for subbing which reads the flag.
            /// </remarks>
            public bool IsFullEnergy;

            /// <summary>
            /// Whether the <see cref="Player"/>'s last known position is within the play area.
            /// Used to determine if a team has been knocked out for matches that are configured with a <see cref="MatchBoxConfiguration.PlayAreaMapRegion"/>.
            /// </summary>
            public bool IsInPlayArea;

            /// <summary>
            /// The cutoff timestamp that the player can ship change (after death).
            /// <see langword="null"/> means a ship change is not allowed.
            /// </summary>
            public DateTime? AllowShipChangeExpiration;

            /// <summary>
            /// The reason that the player can ship change until <see cref="AllowShipChangeExpiration"/>.
            /// </summary>
            public AllowShipChangeReason? AllowShipChangeReason;

            /// <summary>
            /// The # of times the player has changed ships since they became eligible to ship change.
            /// </summary>
            public int ShipChangeCount;

            /// <summary>
            /// The action to take when the player is placed in a ship.
            /// This includes: ship changes (regular ESC + # and ?sc), returning to a match after lagout, and subbing in for another player
            /// </summary>
            public ItemsAction ShipChangeItemsAction;

            /// <summary>
            /// What items to restore when a player subs into a slot or subsequently ship changes.
            /// </summary>
            /// <remarks>
            /// Set this when a player subs in.
            /// </remarks>
            public ItemInventory RestoreItems;

            /// <summary>
            /// The timestamp after which a sub no longer qualifies as a DeathSub (get a full ship).
            /// <see langword="null"/> means not eligible for a DeathSub.
            /// </summary>
            public DateTime? DeathSubExpiration;

            /// <summary>
            /// The timestamp of the last death, for DeathSub only.
            /// When a DeathSub is awarded, this field is cleared.
            /// </summary>
            public DateTime? LastDeathForDeathSub;

            /// <summary>
            /// If this slot is subbed, whether it should attempt to perform full sub (new player gets a full ship).
            /// </summary>
            /// <remarks>
            /// League only and only if <see cref="Team.RemainingFullSubs"/> is > 0.
            /// </remarks>
            public bool FullSubEnabled;

            /// <summary>
            /// The # of lives remaining.
            /// </summary>
            public int Lives { get; set; }

            #region Ship/Item counts

            public ShipType Ship { get; set; }

            public ItemInventory Items;

            byte IPlayerSlot.Bursts => Items.Bursts;
            byte IPlayerSlot.Repels => Items.Repels;
            byte IPlayerSlot.Thors => Items.Thors;
            byte IPlayerSlot.Bricks => Items.Bricks;
            byte IPlayerSlot.Decoys => Items.Decoys;
            byte IPlayerSlot.Rockets => Items.Rockets;
            byte IPlayerSlot.Portals => Items.Portals;

            #endregion

            public PlayerSlot(Team team, int slotIdx)
            {
                Team = team ?? throw new ArgumentNullException(nameof(team));
                SlotIdx = slotIdx;
                AvailableSubSlotNode = new(this);
            }

            public void Reset()
            {
                Status = PlayerSlotStatus.None;
                PlayerName = null;
                Player = null;
                PremadeGroupId = null;
                HasLeftMatchArena = false;
                InactiveTimestamp = null;
                LagOuts = 0;
                AvailableSubSlotNode.List?.Remove(AvailableSubSlotNode);
                IsSubRequested = false;
                SubPlayer = null;
                IsFullEnergy = false;
                IsInPlayArea = false;
                AllowShipChangeExpiration = null;
                AllowShipChangeReason = null;
                ShipChangeCount = 0;
                ShipChangeItemsAction = ItemsAction.Full;
                RestoreItems = default;
                DeathSubExpiration = null;
                LastDeathForDeathSub = null;
                FullSubEnabled = false;
                Lives = 0;
                Ship = ShipType.Spec;
                Items = default;
            }
        }

        private struct ItemInventory
        {
            public byte Bursts { get; set; }
            public byte Repels { get; set; }
            public byte Thors { get; set; }
            public byte Bricks { get; set; }
            public byte Decoys { get; set; }
            public byte Rockets { get; set; }
            public byte Portals { get; set; }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The slot the player is assigned.
            /// <para>
            /// This can be used to determine:
            /// which match the player is in,
            /// which team the player is assigned to,
            /// and which slot in the team the player is assigned to.
            /// </para>
            /// </summary>
            public PlayerSlot? AssignedSlot = null;

            /// <summary>
            /// The slot the player is in the process of being subbed into.
            /// </summary>
            public PlayerSlot? SubSlot = null;

            public Team? CaptainOfTeam;

            /// <summary>
            /// The player's next ship.
            /// Used to spawn the player in the ship of their choosing (after death or in the next match).
            /// </summary>
            public ShipType? NextShip = null;

            /// <summary>
            /// Whether the player has fully entered the arena (including sending a position packet).
            /// This tells us if the player is able to be placed into a ship.
            /// </summary>
            public bool HasFullyEnteredArena = false;

            /// <summary>
            /// Whether the player is the arena of their current match. This does not indicate the player is full entered, just that they're in the arena.
            /// This is <see langword="false"/> when not in a match.
            /// </summary>
            public bool IsInMatchArena = false;

            /// <summary>
            /// Whether the player is trying to ?return to their match.
            /// </summary>
            public bool IsReturning = false;

            /// <summary>
            /// A flag used to tell whether it's the first arena the player has entered after connecting.
            /// This is so that a player can automatically be sent back to the arena their match is being played in after reconnecting.
            /// </summary>
            public bool IsInitialConnect = false;

            /// <summary>
            /// Whether extra position data is being watched for the player via <see cref="IGame.AddExtraPositionDataWatch(Player)"/>.
            /// </summary>
            public bool IsWatchingExtraPositionData = false;

            /// <summary>
            /// Whether the player has overriden spawn position settings.
            /// </summary>
            public bool IsSpawnOverriden = false;

            #region Match startup

            /// <summary>
            /// Whether the player has indicated they're ready for the match to begin.
            /// The player needs to either rotate their ship or fire a weapon.
            /// Moving is not enough since they could have been repelled.
            /// </summary>
            public bool IsReadyToStart = false;

            /// <summary>
            /// The player's last rotation value from their position packet.
            /// When it changes from a non-null value to a different non-null value,
            /// we can consider the player to be ready.
            /// </summary>
            public sbyte? LastRotation = null;

            #endregion

            bool IResettable.TryReset()
            {
                AssignedSlot = null;
                SubSlot = null;
                CaptainOfTeam = null;
                NextShip = null;
                HasFullyEnteredArena = false;
                IsInMatchArena = false;
                IsReturning = false;
                IsInitialConnect = false;
                IsWatchingExtraPositionData = false;
                IsSpawnOverriden = false;
                IsReadyToStart = false;
                LastRotation = null;
                return true;
            }
        }

        private readonly struct ShipSettings
        {
            public byte InitialBurst { get; init; }
            public byte InitialRepel { get; init; }
            public byte InitialThor { get; init; }
            public byte InitialBrick { get; init; }
            public byte InitialDecoy { get; init; }
            public byte InitialRocket { get; init; }
            public byte InitialPortal { get; init; }

            public short MaximumEnergy { get; init; }
        }

        private class ArenaBaseData
        {
            /// <summary>
            /// For assigning freqs to <see cref="MatchData"/>.
            /// </summary>
            /// <remarks>Freqs 0-9 reserved for public play. Therefore, starts at 10.</remarks>
            public short NextFreq = 10;

            /// <summary>
            /// The default queue, so that player can just type ?next (without a queue name).
            /// </summary>
            public string? DefaultQueueName;
        }

        /// <summary>
        /// Data for each arena that has a team versus match type
        /// </summary>
        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? IFreqManagerEnforcerAdvisorToken;
            public InterfaceRegistrationToken<ILeagueHelp>? ILeagueHelpToken;
            public readonly ShipSettings[] ShipSettings = new ShipSettings[8];

            /// <summary>
            /// Whether arenas of this support public play in addition to team versus matches.
            /// </summary>
            public bool PublicPlayEnabled = false;

            /// <summary>
            /// A league match reserves control over the entire arena.
            /// </summary>
            public MatchData? LeagueMatch;

            public string? ReplayRecordingFilePath;

            bool IResettable.TryReset()
            {
                IFreqManagerEnforcerAdvisorToken = null;
                ILeagueHelpToken = null;
                Array.Clear(ShipSettings);
                PublicPlayEnabled = false;
                ReplayRecordingFilePath = null;

                return true;
            }
        }

        private static class CommandNames
        {
            public const string RequestSub = "requestsub";
            public const string Sub = "sub";
            public const string CancelSub = "cancelsub";
            public const string Return = "return";
            public const string Restart = "restart";
            public const string Randomize = "randomize";
            public const string End = "end";
            public const string Draw = "draw";
            public const string ShipChange = "sc";
            public const string Items = "items";
            public const string MatchInfo = "matchinfo";
            public const string FreqInfo = "freqinfo";
            public const string Rosters = "rosters";

            // League specific commands
            public const string LeagueHelp = "leaguehelp";
            public const string Cap = "cap";
            public const string Ready = "ready";
            public const string Rdy = "rdy";
            public const string ForceStart = "forcestart";
            public const string AllowPlay = "allowplay";
            public const string AllowSub = "allowsub";
            public const string FullSub = "fullsub";
        }

        private record struct RosterItem(string PlayerName, bool IsCaptain);

        #endregion
    }
}
