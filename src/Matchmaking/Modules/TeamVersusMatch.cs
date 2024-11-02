using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.Queues;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    public class TeamVersusMatch : IAsyncModule, IMatchmakingQueueAdvisor, IFreqManagerEnforcerAdvisor
    {
        private const string ConfigurationFileName = $"{nameof(TeamVersusMatch)}.conf";

        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IClientSettings _clientSettings;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapData _mapData;
        private readonly IMatchmakingQueues _matchmakingQueues;
        private readonly INetwork _network;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;

        // optional
        private ITeamVersusStatsBehavior? _teamVersusStatsBehavior;

        private AdvisorRegistrationToken<IMatchmakingQueueAdvisor>? _iMatchmakingQueueAdvisorToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private ClientSettingIdentifier _killEnterDelayClientSettingId;

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
        private readonly Dictionary<string, ArenaBaseData> _arenaBaseDataDictionary = new();

        /// <summary>
        /// Data per-arena (not all arenas, only those configured for matches).
        /// </summary>
        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new();

        private readonly DefaultObjectPool<ArenaData> _arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);
        private readonly DefaultObjectPool<TeamLineup> _teamLineupPool = new(new DefaultPooledObjectPolicy<TeamLineup>(), Constants.TargetPlayerCount);
        private readonly DefaultObjectPool<List<TeamLineup>> _teamLineupListPool = new(new ListPooledObjectPolicy<TeamLineup>(), 8);
        private readonly DefaultObjectPool<List<Player>> _playerListPool = new(new ListPooledObjectPolicy<Player>() { InitialCapacity = Constants.TargetPlayerCount }, 8);

        public TeamVersusMatch(
            IComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            IMatchmakingQueues matchmakingQueues,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
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

            if (!_clientSettings.TryGetSettingsIdentifier("Kill", "EnterDelay", out _killEnterDelayClientSettingId))
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

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
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

            return Task.FromResult(true);
        }

        #endregion

        #region IFreqManagerEnforcerAdvsor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return ShipMask.None;

            // When in a match, allow a player to do a normal ship change instead of using ?sc
            // If during a period they can ship change (start of the match or after death), then allow (ShipMask.All).
            // If during a period they cannot ship change, but have additional lives, don't allow, but set their next ship.

            PlayerSlot? playerSlot = playerData.AssignedSlot;
            if (playerSlot is not null // player is in a match
                && player.Arena is not null // player is in an arena
                && player.Arena == playerSlot.MatchData.Arena // player is in the match's arena
                && (IsStartingPhase(playerSlot.MatchData.Status)
                    || (playerSlot.MatchData.Status == MatchStatus.InProgress
                        && playerSlot.AllowShipChangeExpiration is not null && playerSlot.AllowShipChangeExpiration > DateTime.UtcNow
                    )  // is within the period that ship changes are allowed (e.g. starting phase or after a death)
                ))
            {
                return ShipMask.All;
            }

            if (ship != ShipType.Spec) // should not possible to be spec, but checking just in case
            {
                playerData.NextShip = ship;
                _chat.SendMessage(player, $"Your next ship will be a {ship}.");
            }

            return player.Ship.GetShipMask(); // Only allow the current ship. In other words, no change allowed.
        }

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder? errorMessage)
        {
            // Manual freq changes are not allowed.
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.CanEnterGame(Player player, StringBuilder? errorMessage)
        {
            // Entering the game manually is not allowed.
            // Players need to use the matchmaking system commands: ?next, ?sub, ?return
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.IsUnlocked(Player player, StringBuilder? errorMessage)
        {
            return true;
        }

        #endregion

        #region Callbacks

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
                ArenaData arenaData = _arenaDataPool.Get();
                _arenaDataDictionary.Add(arena, arenaData);

                // Read ship settings from the config.
                string[] shipNames = Enum.GetNames<ShipType>();
                for (int i = 0; i < 8; i++)
                {
                    ConfigHandle ch = arena.Cfg!;

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
                _commandManager.AddCommand(CommandNames.Restart, Command_restart, arena);
                _commandManager.AddCommand(CommandNames.Randomize, Command_randomize, arena);
                _commandManager.AddCommand(CommandNames.End, Command_end, arena);
                _commandManager.AddCommand(CommandNames.Draw, Command_draw, arena);
                _commandManager.AddCommand(CommandNames.ShipChange, Command_sc, arena);
                _commandManager.AddCommand(CommandNames.Items, Command_items, arena);

                // Register advisor.
                arenaData.IFreqManagerEnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

                // Fill in arena for associated matches.
                foreach (MatchData matchData in _matchDataDictionary.Values)
                {
                    if (matchData.Arena is null && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchData.Arena = arena;
                    }
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!_arenaDataDictionary.Remove(arena, out ArenaData? arenaData))
                    return;

                try
                {
                    // Unregister advisor.
                    if (!arena.UnregisterAdvisor(ref arenaData.IFreqManagerEnforcerAdvisorToken))
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
                    _commandManager.RemoveCommand(CommandNames.Restart, Command_restart, arena);
                    _commandManager.RemoveCommand(CommandNames.Randomize, Command_randomize, arena);
                    _commandManager.RemoveCommand(CommandNames.End, Command_end, arena);
                    _commandManager.RemoveCommand(CommandNames.Draw, Command_draw, arena);
                    _commandManager.RemoveCommand(CommandNames.ShipChange, Command_sc, arena);
                    _commandManager.RemoveCommand(CommandNames.Items, Command_items, arena);
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
                    else
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
                if (slot is not null)
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
            killerPlayerSlot.Team.Score++;

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
            else
            {
                // The slot still has lives, allow the player to ship change (after death) for a limited amount of time.
                killedPlayerSlot.AllowShipChangeExpiration = now + matchData.Configuration.AllowShipChangeAfterDeathDuration; // TODO: maybe limit how many changes a player can make?
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
                StringBuilder gameTimeBuilder = _objectPoolManager.StringBuilderPool.Get();
                HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    TimeSpan gameTime = matchData.Started is not null ? now - matchData.Started.Value : TimeSpan.Zero;
                    gameTimeBuilder.AppendFriendlyTimeSpan(gameTime);

                    GetPlayersToNotify(matchData, notifySet);

                    // Kill notification
                    _chat.SendSetMessage(notifySet, $"{killedName} kb {killerName}");

                    // Remaining lives notification
                    if (isKnockout)
                    {
                        _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killedName} is OUT! [{gameTimeBuilder}]");
                    }
                    else
                    {
                        _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killedName} has {killedPlayerSlot.Lives} {(killedPlayerSlot.Lives > 1 ? "lives" : "life")} remaining [{gameTimeBuilder}]");
                    }

                    // Score notification
                    StringBuilder remainingBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        short highScore = -1;
                        short highScoreFreq = -1;
                        int highScoreCount = 0;

                        foreach (var team in matchData.Teams)
                        {
                            if (sb.Length > 0)
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

                        _chat.SendSetMessage(notifySet, $"Score: {sb} -- {remainingBuilder} -- [{gameTimeBuilder}]");
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(remainingBuilder);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifySet);
                    _objectPoolManager.StringBuilderPool.Return(gameTimeBuilder);
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }


            bool MainloopTimer_ProcessKnockOut(PlayerSlot slot)
            {
                if (slot.Player is not null && slot.Player.Ship != ShipType.Spec)
                {
                    _game.SetShip(slot.Player, ShipType.Spec);
                }

                return false;
            }
        }

        // This is called synchronously when the Game module sets a player's ship/freq.
        private void Callback_PreShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            if (slot.MatchData.Status == MatchStatus.InProgress
                && newShip != ShipType.Spec
                && oldShip != ShipType.Spec
                && newShip != oldShip
                && newFreq == oldFreq)
            {
                // Send ship change notification.
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(slot.MatchData, players);

                    if (players.Count > 0)
                    {
                        _chat.SendSetMessage(players, $"{slot.Player!.Name} changed to a {slot.Player.Ship}");
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

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

                if (slot.Bursts != extra.Bursts)
                {
                    slot.Bursts = extra.Bursts;
                    changes |= ItemChanges.Bursts;
                }

                if (slot.Repels != extra.Repels)
                {
                    slot.Repels = extra.Repels;
                    changes |= ItemChanges.Repels;
                }

                if (slot.Thors != extra.Thors)
                {
                    slot.Thors = extra.Thors;
                    changes |= ItemChanges.Thors;
                }

                if (slot.Bricks != extra.Bricks)
                {
                    slot.Bricks = extra.Bricks;
                    changes |= ItemChanges.Bricks;
                }

                if (slot.Decoys != extra.Decoys)
                {
                    slot.Decoys = extra.Decoys; 
                    changes |= ItemChanges.Decoys;
                }

                if (slot.Rockets != extra.Rockets)
                {
                    slot.Rockets = extra.Rockets; 
                    changes |= ItemChanges.Rockets;
                }

                if (slot.Portals != extra.Portals)
                {
                    slot.Portals = extra.Portals;
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
            if (slot is null || slot.Status != PlayerSlotStatus.Playing)
                return;

            bool isAfterDeath = (reasons & SpawnCallback.SpawnReason.AfterDeath) == SpawnCallback.SpawnReason.AfterDeath;
            bool isShipChange = (reasons & SpawnCallback.SpawnReason.ShipChange) == SpawnCallback.SpawnReason.ShipChange;

            if (isAfterDeath
                && !isShipChange
                && playerData.NextShip is not null
                && player.Ship != playerData.NextShip)
            {
                // The player respawned after dying and has a different ship set as their next one.
                // Change the player to that ship.
                _game.SetShip(player, playerData.NextShip.Value); // this will trigger another Spawn callback
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

                    string queueName = matchConfiguration.QueueName;
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

                    _matchConfigurationDictionary[matchType] = matchConfiguration;

                    // Remove existing matches.
                    // Only remove inactive ones.
                    // Ongoing matches will continue to run with their old configuration, but will be discarded when they complete.

                    List<MatchIdentifier> toRemove = new();
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

            string queueName = matchConfiguration.QueueName;
            if (_queueMatchConfigurations.TryGetValue(queueName, out List<MatchConfiguration>? matchConfigurationList))
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

            List<MatchIdentifier> toRemove = new();
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
            Targets = CommandTarget.None,
            Args = null,
            Description = "For a player to request to be subbed out of their current match.")]
        private void Command_requestsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            if (slot.Lives <= 0)
            {
                // The slot has already been knocked out.
                _chat.SendMessage(player, "Your assigned slot is not eligible for a sub since it's knocked out.");
                return;
            }

            if (slot.AvailableSubSlotNode.List is not null || slot.IsSubRequested)
            {
                // The slot is already open for a sub.
                _chat.SendMessage(player, "Your assigned slot is already open for a sub.");
                return;
            }

            slot.IsSubRequested = true;
            _availableSubSlots.AddLast(slot.AvailableSubSlotNode);

            _chat.SendArenaMessage(player.Arena, $"{player.Name} has requested to be subbed out.");
            SendSubAvailabilityNotificationToQueuedPlayers(slot.MatchData);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<queue name>]",
            Description = """
                Searches for the next available slot in any ongoing matches and substitutes the player into the slot.
                When matched with a slot to sub in for, the switch may not happen immediately.
                First, the player may need to be moved to the proper arena.
                Also, the slot may currently contain an active player (that requested to be subbed out),
                in which case the active player needs to get to full energy to become eligible to be switched out.
                """)]
        private void Command_sub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // TODO: maybe the sub command should be in the MatchMakingQueues module, and this method is an advisor method?

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

            // TODO: Maybe allow players that are not in the matchmaking queue to sub in?

            // Find the next available slot, if one exists.
            PlayerSlot? subSlot = null;

            foreach (PlayerSlot slot in _availableSubSlots)
            {
                if (slot.CanBeSubbed
                    && slot.SubPlayer is null // no sub in progress
                    && (parameters.IsWhiteSpace() || parameters.Equals(slot.MatchData.Configuration.QueueName, StringComparison.OrdinalIgnoreCase))
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
            Arena? arena = _arenaManager.FindArena(matchData.ArenaName);
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

        private void SubSlot(PlayerSlot slot)
        {
            if (slot is null)
                return;

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
                && !slot.IsFullEnergy) // Note: not using slot.Player.Position.Energy since it might not be up to date (depending on the order of position packet handlers)
            {
                // The currently assigned player is still playing and isn't at full energy.
                // Wait until the player gets to full energy.

                // This method will be called again by:
                // - the position packet handler (when the currently assigned player's energy gets to max)
                // - the player action handler (when the currently assigned player leaves the arena)
                // - the ship change handler (when the currently assigned player switches to spec)

                _chat.SendMessage(slot.Player, $"A player is waiting to sub in for your slot. Get to full energy to be automatically subbed out.");
                _chat.SendMessage(player, "You will be subbed in when the active player gets to full energy. Please stand by.");
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
            AssignSlot(slot, player);
            SetShipAndFreq(slot, true, null);

            TeamVersusMatchPlayerSubbedCallback.Fire(arena, slot, subOutPlayerName);
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
            if (slot is null)
                return;

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

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
            {
                _chat.SendMessage(player, $"You are not in a match.");
                return;
            }

            MatchData matchData = slot.MatchData;
            if (matchData.Status != MatchStatus.Initializing && matchData.Status != MatchStatus.InProgress)
            {
                return;
            }

            if (slot.LagOuts >= matchData.Configuration.MaxLagOuts)
            {
                _chat.SendMessage(player, $"You cannot return to the match because you have exceeded the maximum # of LagOuts: {matchData.Configuration.MaxLagOuts}.");
                return;
            }

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

            PlayerSlot? slot = playerData.AssignedSlot;
            if (slot is null)
                return; // Another player may have subbed in.

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

            SetShipAndFreq(slot, true, null);

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetPlayersToNotify(slot.MatchData, players);

                if (slot.SubPlayer is not null)
                {
                    players.Add(slot.SubPlayer);
                }

                if (players.Count > 0)
                {
                    _chat.SendSetMessage(players, $"{player.Name} returned to the match. [Lives: {slot.Lives}]");
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
            Description = "Request a ship change. It will be allowed after death. Otherwise, it sets the next ship to use upon death.")]
        private void Command_sc(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerSlot? playerSlot = playerData.AssignedSlot;
            if (playerSlot is null)
            {
                _chat.SendMessage(player, "You're not playing in a match.");
                return;
            }

            if (player.Arena is null
                || !string.Equals(player.Arena.Name, playerSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"Your match is in a different arena: ?go {playerSlot.MatchData.ArenaName}");
                return;
            }

            if (playerSlot.AllowShipChangeExpiration is not null && playerSlot.AllowShipChangeExpiration > DateTime.UtcNow)
            {
                if (!TryParseShipNumber(player, parameters, out int shipNumber))
                    return;

                ShipType ship = (ShipType)(shipNumber - 1);
                _game.SetShip(player, ship);
            }
            else if (playerSlot.Lives > 0)
            {
                if (!TryParseShipNumber(player, parameters, out int shipNumber))
                    return;

                ShipType ship = (ShipType)(shipNumber - 1);
                playerData.NextShip = ship;

                _chat.SendMessage(player, $"Your next ship will be a {ship}.");
            }

            bool TryParseShipNumber(Player player, ReadOnlySpan<char> s, out int shipNumber)
            {
                if (!int.TryParse(s, out shipNumber) || shipNumber < 1 || shipNumber > 8)
                {
                    _chat.SendMessage(player, "Invalid ship type specified.");
                    return false;
                }

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
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            MatchData? matchData = playerData.AssignedSlot?.MatchData;
            if (matchData is null)
            {
                // TODO: Check if the player is spectating a player in a match
            }

            if (matchData is null)
                return;

            if (matchData.Status != MatchStatus.InProgress)
                return;

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
                                    teamBuilder.Append("  ");

                                teamBuilder.Append($"{slot.PlayerName}: {slot.Repels}/{slot.Rockets}");
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

                    string queueName = matchConfiguration.QueueName;
                    if (!_queueDictionary.TryGetValue(queueName, out TeamVersusMatchmakingQueue? queue))
                    {
                        queue = CreateQueue(ch, queueName);
                        if (queue is null)
                            continue;

                        // TODO: for now only allowing groups of the exact size needed (for simplified matching)
                        if (queue.Options.MinGroupSize != matchConfiguration.PlayersPerTeam
                            || queue.Options.MaxGroupSize != matchConfiguration.PlayersPerTeam)
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
            int gameTypeId = _configManager.GetInt(ch, matchType, "GameTypeId", -1);
            if (gameTypeId == -1)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Missing or invalid MatchTypeId for Match '{matchType}'.");
                return null;
            }

            string? queueName = _configManager.GetStr(ch, matchType, "Queue");
            if (string.IsNullOrWhiteSpace(queueName))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid Queue for Match '{matchType}'.");
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

            int inactiveSlotAvailableDelaySeconds = _configManager.GetInt(ch, matchType, "InactiveSlotAvailableDelay", 30);
            TimeSpan inactiveSlotAvailableDelay = TimeSpan.FromSeconds(inactiveSlotAvailableDelaySeconds);

            ItemsCommandOption itemsCommandOption = _configManager.GetEnum(ch, matchType, "ItemsCommandOption", ItemsCommandOption.None);

            MatchConfiguration matchConfiguration = new()
            {
                MatchType = matchType,
                GameTypeId = gameTypeId,
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
                TimeLimitWinBy = timeLimitWinBy,
                MaxLagOuts = maxLagOuts,
                AllowShipChangeAfterDeathDuration = allowShipChangeAfterDeathDuration,
                InactiveSlotAvailableDelay = inactiveSlotAvailableDelay,
                ItemsCommandOption = itemsCommandOption,
                Boxes = new MatchBoxConfiguration[numBoxes],
            };

            if (!LoadMatchBoxesConfiguration(ch, matchType, matchConfiguration))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Invalid configuration of boxes for Match '{matchType}'.");
                return null;
            }

            return matchConfiguration;

            bool LoadMatchBoxesConfiguration(ConfigHandle ch, string matchIdentifier, MatchConfiguration matchConfiguration)
            {
                List<TileCoordinates> tempCoordList = new();

                for (int boxIdx = 0; boxIdx < matchConfiguration.Boxes.Length; boxIdx++)
                {
                    int boxNumber = boxIdx + 1; // configuration is 1 based
                    string boxSection = $"{matchIdentifier}-Box{boxNumber}";

                    string? playAreaMapRegion = _configManager.GetStr(ch, boxSection, "PlayAreaMapRegion");

                    MatchBoxConfiguration boxConfiguration = new()
                    {
                        TeamStartLocations = new TileCoordinates[matchConfiguration.NumTeams][],
                        PlayAreaMapRegion = playAreaMapRegion,
                    };

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
                                _logManager.LogM(LogLevel.Warn, nameof(TeamVersusMatch), $"Invalid starting location for Match '{matchIdentifier}', Box:{boxNumber}, Team:{teamNumber}, #:{coordId}.");
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
                    Dictionary<string, Player> playersByName = new(); // TODO: pool
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
                    // Hold onto the task so that each time we try to proceed further (e.g. a player enters the arena), we can check whether it's done.
                    matchData.InitializeStatsTask = _teamVersusStatsBehavior.InitializeAsync(matchData);
                }

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

            bool TryGetAvailableMatch(MatchConfiguration matchConfiguration, [MaybeNullWhen(false)] out MatchData matchData)
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
                            return true;
                    }
                }

                // no availability
                matchData = null;
                return false;
            }
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
                    if (responsibleBuilder.Length > 0)
                        responsibleBuilder.Append(", ");

                    responsibleBuilder.Append(playerName);
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

            if (slot.PlayerName is not null && !string.Equals(slot.PlayerName, player.Name, StringComparison.OrdinalIgnoreCase))
            {
                // The slot was previously assigned to another player. Send a notification.
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetPlayersToNotify(slot.MatchData, players);
                    players.Add(player);

                    if (players.Count > 0)
                    {
                        StringBuilder gameTimeBuilder = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            MatchData matchData = slot.MatchData;
                            TimeSpan gameTime = matchData.Started is not null ? DateTime.UtcNow - matchData.Started.Value : TimeSpan.Zero;
                            gameTimeBuilder.AppendFriendlyTimeSpan(gameTime);

                            _chat.SendSetMessage(players, $"{player.Name} in for {slot.PlayerName} -- {slot.Lives} {(slot.Lives == 1 ? "life" : "lives")} [{gameTime}]");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(gameTimeBuilder);
                        }
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
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
            slot.PlayerName = player.Name!;
            slot.Player = player;
            playerData.AssignedSlot = slot;

            // Clear fields now that the slot is newly assigned.
            slot.LagOuts = 0;
            slot.IsSubRequested = false;
        }

        private void UnassignSlot(PlayerSlot slot)
        {
            if (slot is null)
                return;

            if (string.IsNullOrWhiteSpace(slot.PlayerName))
                return; // The slot is not assigned to a player.

            slot.PlayerName = null;

            if (slot.Player is not null)
            {
                if (slot.Player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                {
                    playerData.AssignedSlot = null;
                }

                slot.Player = null;
            }
        }

        private void SetSlotInactive(PlayerSlot slot, SlotInactiveReason reason)
        {
            if (slot.Status != PlayerSlotStatus.Playing)
                return;

            slot.Status = PlayerSlotStatus.Waiting;
            slot.InactiveTimestamp = DateTime.UtcNow;
            slot.LagOuts++;

            MatchData matchData = slot.MatchData;

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

                    if (remainingTeamCount <= 1)
                    {
                        if (remainingTeamCount == 0)
                            _chat.SendSetMessage(players, $"There are no remaining active teams. The game with automatically end in 15 seconds if not refilled.");
                        else
                            _chat.SendSetMessage(players, $"There is {remainingTeamCount} remaining active team. The game with automatically end in 15 seconds if not refilled.");

                        // Schedule a timer to check for match completion.
                        // The timer is to allow player(s) to ?return before the check happens. (e.g. server lag spike kicking all players to spec)
                        ScheduleCheckForMatchCompletion(matchData, 15000); // TODO: configuration setting (remember to change the chat notification too)
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
            if (!_queueDictionary.TryGetValue(matchData.Configuration.QueueName, out var queue))
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
        /// This includes the players in the match, and any spectators in that match's arena.
        /// </summary>
        /// <param name="matchData">The match.</param>
        /// <param name="players">The collection to populate with players.</param>
        private void GetPlayersToNotify(MatchData matchData, HashSet<Player> players)
        {
            if (matchData is null || players is null)
                return;

            // Players in the match.
            foreach (Team team in matchData.Teams)
            {
                foreach (PlayerSlot slot in team.Slots)
                {
                    if (slot.Player is not null)
                    {
                        players.Add(slot.Player);
                    }
                }
            }

            // Players in the arena and on the spec freq get notifications for all matches in the arena.
            // Players on a team freq get messages for the associated match (this includes a players that got subbed out).
            Arena? arena = _arenaManager.FindArena(matchData.ArenaName);
            if (arena is not null)
            {
                _playerData.Lock();

                try
                {
                    Span<short> matchFreqs = stackalloc short[matchData.Teams.Length];
                    for (int teamIdx = 0; teamIdx < matchData.Teams.Length; teamIdx++)
                    {
                        matchFreqs[teamIdx] = matchData.Teams[teamIdx].Freq;
                    }

                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena // in the arena
                            && (player.Freq == arena.SpecFreq // on the spec freq
                                || matchFreqs.Contains(player.Freq) // or on a team freq
                            ))
                        {
                            players.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
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
                MatchStatus.Initializing or MatchStatus.StartingCheck or MatchStatus.StartingCountdown or MatchStatus.InProgress or MatchStatus.Complete => true,
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

                if (matchData.InitializeStatsTask is not null)
                {
                    if (matchData.InitializeStatsTask.IsCompleted)
                    {
                        matchData.InitializeStatsTask = null;
                    }
                    else
                    {
                        // Can't start yet, stats initialization is still in progress.
                        return;
                    }
                }

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

                    // Set freqs and ships, and move players to their starting locations.
                    foreach (Team team in matchData.Teams)
                    {
                        TileCoordinates[] startLocations = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[team.TeamIdx];
                        int startLocationIdx = startLocations.Length == 1 ? 0 : _prng.Number(0, startLocations.Length - 1);
                        TileCoordinates startLocation = startLocations[startLocationIdx];

                        foreach (PlayerSlot playerSlot in team.Slots)
                        {
                            Player player = playerSlot.Player!;
                            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                                continue;

                            playerSlot.Lives = matchData.Configuration.LivesPerPlayer;
                            playerSlot.LagOuts = 0;
                            playerSlot.AllowShipChangeExpiration = null;

                            SetShipAndFreq(playerSlot, false, startLocation);
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
                            // They are penalized with a delay to discourage from attempting to cherrypick their matches (didn't like their teammates) or trolling.
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

                    // Reset ships
                    foreach (Player player in readyPlayers)
                    {
                        _game.ShipReset(player);
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

                // Run the starting countdown.
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

                        _chat.SendSetMessage(notifyCountdownPlayers, ChatSound.Ding, "GO!");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(notifyCountdownPlayers);
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

                    // Tell the stats module that the match has started.
                    if (_teamVersusStatsBehavior is not null)
                    {
                        await _teamVersusStatsBehavior.MatchStartedAsync(matchData);
                    }

                    TeamVersusMatchStartedCallback.Fire(matchData.Arena!, matchData);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                    _objectPoolManager.PlayerSetPool.Return(abandonedPlayers);
                    _objectPoolManager.NameHashSetPool.Return(abandonedPlayerNames);
                }
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

        private void SetShipAndFreq(PlayerSlot slot, bool isRefill, TileCoordinates? startLocation)
        {
            Player? player = slot.Player;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            ShipType ship;
            if (isRefill)
            {
                ship = slot.Ship;
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

            // Spawn the player (this is automatically in the center).
            _game.SetShipAndFreq(player, ship, slot.Team.Freq);

            // Warp the player to the starting location.
            if (startLocation is not null)
            {
                _game.WarpTo(player, startLocation.Value.X, startLocation.Value.Y);
            }

            if (isRefill)
            {
                // Adjust the player's items to the prior remaining amounts.
                SetRemainingItems(slot);
            }

            // Remove any existing timers for the slot.
            _mainloopTimer.ClearTimer<PlayerSlot>(MainloopTimer_ProcessInactiveSlot, slot);

            void SetRemainingItems(PlayerSlot slot)
            {
                Player? player = slot.Player;
                if (player is null)
                    return;

                Arena? arena = player.Arena;
                if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                    return;

                ref ShipSettings shipSettings = ref arenaData.ShipSettings[(int)slot.Ship];
                AdjustItem(player, Prize.Burst, shipSettings.InitialBurst, slot.Bursts);
                AdjustItem(player, Prize.Repel, shipSettings.InitialRepel, slot.Repels);
                AdjustItem(player, Prize.Thor, shipSettings.InitialThor, slot.Thors);
                AdjustItem(player, Prize.Brick, shipSettings.InitialBrick, slot.Bricks);
                AdjustItem(player, Prize.Decoy, shipSettings.InitialDecoy, slot.Decoys);
                AdjustItem(player, Prize.Rocket, shipSettings.InitialRocket, slot.Rockets);
                AdjustItem(player, Prize.Portal, shipSettings.InitialPortal, slot.Portals);

                void AdjustItem(Player player, Prize prize, byte initial, byte remaining)
                {
                    short adjustAmount = (short)(initial - remaining);
                    if (adjustAmount <= 0)
                        return;

                    _game.GivePrize(player, (Prize)(-(short)prize), adjustAmount);
                }
            }
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
                        GetPlayersToNotify(matchData, notifySet);

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

                TeamVersusMatchEndedCallback.Fire(arena ?? _broker, matchData, reason, winnerTeam);

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

            // Clear the team data.
            foreach (Team team in matchData.Teams)
            {
                // Slots
                foreach (PlayerSlot slot in team.Slots)
                {
                    UnassignSlot(slot); // this clears slot.PlayerName and slot.Player
                    slot.Status = PlayerSlotStatus.None;
                    slot.PremadeGroupId = null;
                    slot.HasLeftMatchArena = false;
                    slot.InactiveTimestamp = null;
                    slot.LagOuts = 0;
                    slot.AvailableSubSlotNode.List?.Remove(slot.AvailableSubSlotNode);
                    slot.IsSubRequested = false;
                    // Note: slot.SubPlayer should already be null because we would have called CancelSubInProgress
                    slot.IsFullEnergy = false;
                    slot.IsInPlayArea = false;
                    slot.AllowShipChangeExpiration = null;
                    slot.Lives = 0;

                    // ship and items
                    slot.Ship = ShipType.Warbird;
                    slot.Bursts = 0;
                    slot.Repels = 0;
                    slot.Thors = 0;
                    slot.Bricks = 0;
                    slot.Decoys = 0;
                    slot.Rockets = 0;
                    slot.Portals = 0;
                }

                // Score
                team.Score = 0;
            }

            // Set the status. This makes it available to host a new game.
            matchData.Status = MatchStatus.None;
            matchData.PhaseExpiration = null;
            matchData.StartCountdown = 0;
            matchData.Started = null;
            matchData.IsOvertime = false;

            if (!_matchConfigurationDictionary.TryGetValue(matchData.MatchIdentifier.MatchType, out MatchConfiguration? configuration)
                || configuration != matchData.Configuration)
            {
                // The configuration has changed.
                // Discard the MatchData. This will allow a new one to be created with the new configuration.
                _matchDataDictionary.Remove(matchData.MatchIdentifier);

                return;
            }

            // Now that the match has ended, check if there are enough players available to refill it.
            _mainloop.QueueMainWorkItem(MainloopWork_MakeMatch, matchData.Configuration.QueueName);


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

        private class MatchConfiguration : IMatchConfiguration
        {
            public required string MatchType { get; init; }
            public required long GameTypeId { get; init; }
            public required string QueueName { get; init; }
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
            public required int TimeLimitWinBy { get; init; }
            public required int MaxLagOuts { get; init; }
            public required TimeSpan AllowShipChangeAfterDeathDuration { get; init; }
            public required TimeSpan InactiveSlotAvailableDelay { get; init; }
            public ItemsCommandOption ItemsCommandOption { get; init; } = ItemsCommandOption.None;

            public required MatchBoxConfiguration[] Boxes;
        }

        private class MatchBoxConfiguration
        {
            /// <summary>
            /// Available starting locations for each team.
            /// </summary>
            public required TileCoordinates[][] TeamStartLocations;

            public string? PlayAreaMapRegion;
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
            /// Wait for every player to enter the designated arena and send a position packet (finished map and lvz download).
            /// If any player takes too long, leaves the arena, or disconnects, cancel the match and send the remaining players back to the front of the queue.
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
            /// The match is ongoing.
            /// </summary>
            InProgress,

            /// <summary>
            /// The match is complete. A bit of time will be given before the next match.
            /// </summary>
            Complete,
        }

        private class MatchData : IMatchData
        {
            public MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration, short startingFreq)
            {
                MatchIdentifier = matchIdentifier;
                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ArenaName = Arena.CreateArenaName(Configuration.ArenaBaseName, MatchIdentifier.ArenaNumber);
                Status = MatchStatus.None;
                Teams = new Team[Configuration.NumTeams];
                _readOnlyTeams = new ReadOnlyCollection<ITeam>(Teams);

                for (int teamIdx = 0; teamIdx < Configuration.NumTeams; teamIdx++)
                {
                    Teams[teamIdx] = new Team(this, teamIdx, startingFreq++);
                }

                ParticipationList = new(Configuration.NumTeams * Configuration.PlayersPerTeam * 2);
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
            /// Task that initializes the stats module prior to a match starting.
            /// </summary>
            public Task? InitializeStatsTask;

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

            public bool IsPremade { get; set; }

            public short Score { get; set; }
        }

        private enum PlayerSlotStatus
        {
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
        /// A slot for a player on a team.
        /// </summary>
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
            public bool CanBeSubbed =>
                IsSubRequested
                || (Status == PlayerSlotStatus.Waiting
                    && (LagOuts >= MatchData.Configuration.MaxLagOuts
                        || (InactiveTimestamp is not null
                            && (DateTime.UtcNow - InactiveTimestamp.Value) >= MatchData.Configuration.InactiveSlotAvailableDelay
                        )
                    )
                );

            /// <summary>
            /// The player that is in the process of being subbed into the slot.
            /// </summary>
            public Player? SubPlayer;

            /// <summary>
            /// Whether the <see cref="Player"/> is at full energy.
            /// This is used when trying to sub a player in.
            /// </summary>
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
            /// The # of lives remaining.
            /// </summary>
            public int Lives { get; set; }

            #region Ship/Item counts

            public ShipType Ship { get; set; }
            public byte Bursts { get; set; }
            public byte Repels { get; set; }
            public byte Thors { get; set; }
            public byte Bricks { get; set; }
            public byte Decoys { get; set; }
            public byte Rockets { get; set; }
            public byte Portals { get; set; }

            #endregion

            public PlayerSlot(Team team, int slotIdx)
            {
                Team = team ?? throw new ArgumentNullException(nameof(team));
                SlotIdx = slotIdx;
                AvailableSubSlotNode = new(this);
            }
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
                NextShip = null;
                HasFullyEnteredArena = false;
                IsInMatchArena = false;
                IsReturning = false;
                IsInitialConnect = false;
                IsWatchingExtraPositionData = false;
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
            public short NextFreq = 0;

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
            public readonly ShipSettings[] ShipSettings = new ShipSettings[8];

            bool IResettable.TryReset()
            {
                IFreqManagerEnforcerAdvisorToken = null;
                Array.Clear(ShipSettings);

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
        }

        private enum SlotInactiveReason
        {
            /// <summary>
            /// The player has changed to spectator mode.
            /// </summary>
            ChangedToSpec,

            /// <summary>
            /// The player has left the arena.
            /// </summary>
            LeftArena,
        }

        #endregion
    }
}
