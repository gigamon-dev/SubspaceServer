using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public class TeamVersusMatch : IModule, IMatchmakingQueueAdvisor, IFreqManagerEnforcerAdvisor
    {
        private const string ConfigurationFileName = $"{nameof(TeamVersusMatch)}.conf";

        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private IGame _game;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IMapData _mapData;
        private IMatchmakingQueues _matchmakingQueues;
        private INetwork _network;
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
        private readonly LinkedList<PlayerSlot> _availableSubSlots = new(); // TODO: pooling of node objects
        // TODO: when a match ends, remember to remove from _availableSubSlots

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

        private readonly ObjectPool<ArenaData> _arenaDataObjectPool = new NonTransientObjectPool<ArenaData>(new ArenaDataPooledObjectPolicy());

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

            if (!LoadConfiguration())
            {
                return false;
            }

            _pdKey = _playerData.AllocatePlayerData(new PlayerDataPooledObjectPolicy());

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Register(broker, Callback_MatchmakingQueueChanged);

            _commandManager.AddCommand("loadmatchtype", Command_loadmatchtype);
            _commandManager.AddCommand("unloadmatchtype", Command_unloadmatchtype);

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken);

            _commandManager.RemoveCommand("loadmatchtype", Command_loadmatchtype);
            _commandManager.RemoveCommand("unloadmatchtype", Command_unloadmatchtype);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvsor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return ShipMask.None;

            // When in a match, allow a player to do a normal ship change instead of using ?sc
            // If during a period they can ship change (start of the match or after death), then allow (ShipMask.All).
            // If during a period they cannot ship change, but have additional lives, don't allow, but set their next ship.

            PlayerSlot playerSlot = playerData.AssignedSlot;
            if (playerSlot is not null // player is in a match
                && player.Arena is not null // player is in an arena
                && string.Equals(player.Arena.Name, playerSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase) // player is in the match's arena
                && playerSlot.AllowShipChangeCutoff is not null && playerSlot.AllowShipChangeCutoff < DateTime.UtcNow)  // within the period that ship changes are allowed (e.g. after death)
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

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder errorMessage)
        {
            // Manual freq changes are not allowed.
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.CanEnterGame(Player player, StringBuilder errorMessage)
        {
            // Entering the game manually is not allowed.
            // Players need to use the matchmaking system commands: ?next, ?sub, ?return
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.IsUnlocked(Player player, StringBuilder errorMessage)
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
                ArenaData arenaData = _arenaDataObjectPool.Get();
                _arenaDataDictionary.Add(arena, arenaData);

                // Read ship settings from the config.
                string[] shipNames = Enum.GetNames<ShipType>();
                for (int i = 0; i < 8; i++)
                {
                    arenaData.ShipSettings[i] = new ShipSettings()
                    {
                        InitialBurst = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialBurst", 0),
                        InitialRepel = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialRepel", 0),
                        InitialThor = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialThor", 0),
                        InitialBrick = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialBrick", 0),
                        InitialDecoy = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialDecoy", 0),
                        InitialRocket = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialRocket", 0),
                        InitialPortal = (byte)_configManager.GetInt(arena.Cfg, shipNames[i], "InitialPortal", 0),
                        MaximumEnergy = (short)_configManager.GetInt(arena.Cfg, shipNames[i], "MaximumEnergy", 0),
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

                // Register advisor.
                arenaData.IFreqManagerEnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

                // Fill in arena for associated matches.
                foreach (MatchData matchData in _matchDataDictionary.Values)
                {
                    if (string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchData.Arena = arena;
                    }
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!_arenaDataDictionary.Remove(arena, out ArenaData arenaData))
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
                }
                finally
                {
                    _arenaDataObjectPool.Return(arenaData);
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

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Connect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                // This flag tells is that the player just connected.
                // We'll use it when the player enters the game.
                playerData.IsInitialConnect = true;
            }
            //else if (action == PlayerAction.EnterArena) // Purposely using EnterGame instead
            //{
            //}
            else if (action == PlayerAction.EnterGame)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                if (playerData.IsInitialConnect)
                {
                    // The player connected and entered an arena.
                    playerData.IsInitialConnect = false;

                    if (_playerSlotDictionary.TryGetValue(player.Name, out PlayerSlot playerSlot))
                    {
                        // The player is associated with an ongoing match.
                        if (string.Equals(playerSlot.PlayerName, player.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // The player is still assigned to the slot.
                            playerData.AssignedSlot = playerSlot;
                            playerSlot.Player = player;
                        }

                        if (!string.Equals(arena.Name, playerSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                        {
                            // The arena the player entered is not the arena their match is in. Send them to the proper arena.
                            _chat.SendMessage(player, "Sending you to your ongoing match's arena. Please stand by...");
                            _arenaManager.SendToArena(player, playerSlot.MatchData.ArenaName, 0, 0);
                            return;
                        }
                    }
                }

                playerData.HasEnteredArena = true;

                PlayerSlot slot = playerData.AssignedSlot;
                if (slot is not null)
                {
                    MatchData matchData = playerData.AssignedSlot.MatchData;

                    if (matchData.Status == MatchStatus.Initializing
                        && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        // The player entered an arena for a match that's starting up.
                        QueueMatchInitialzation(playerData.AssignedSlot.MatchData);
                    }
                    else if (playerData.IsReturning
                        && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
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
                    && string.Equals(arena.Name, playerData.SubSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    // The player entered an arena for a match that they're trying to ?sub in for.
                    SubSlot(playerData.SubSlot);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                playerData.HasEnteredArena = false;

                if (playerData.IsWatchingExtraPositionData)
                {
                    _game.RemoveExtraPositionDataWatch(player);
                    playerData.IsWatchingExtraPositionData = false;
                }

                if (playerData.SubSlot is not null
                    && string.Equals(arena.Name, playerData.SubSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    // The player left the arena before entering the game, and therefore did not complete subbing into the match.
                    CancelSubInProgress(playerData.SubSlot, false);
                }

                PlayerSlot slot = playerData.AssignedSlot;
                if (slot is not null
                    && slot.Status == PlayerSlotStatus.Playing)
                {
                    // The player left the arena while playing in a match.
                    SetSlotInactive(slot, SlotNotActiveReason.LeftArena);
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

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

                PlayerSlot slot = playerData.AssignedSlot;
                if (slot is null)
                    return;

                // The player stays assigned to the slot.
                // However, the player object is no longer available to us.
                slot.Player = null;

                // The PlayerData will get cleared and returned to the pool.
            }
        }

        private void Callback_MatchmakingQueueChanged(IMatchmakingQueue queue, QueueAction action, QueueItemType itemType)
        {
            if (action != QueueAction.Add
                || !_queueDictionary.TryGetValue(queue.Name, out TeamVersusMatchmakingQueue found)
                || found != queue)
            {
                return;
            }

            // TODO: if there's an ongoing game that needs a player, try to get one for it

            // Check if a new match can be started.
            while (MakeMatch(found)) { }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedPlayerData))
                return;

            PlayerSlot killedPlayerSlot = killedPlayerData.AssignedSlot;
            if (killedPlayerSlot is null)
                return;

            if (killedPlayerSlot.MatchData.Status != MatchStatus.InProgress)
                return;

            killedPlayerSlot.Lives--;

            MatchData matchData = killedPlayerSlot.MatchData;

            TimeSpan gameTime = matchData.Started is not null ? DateTime.UtcNow - matchData.Started.Value : TimeSpan.Zero;
            gameTime = new(gameTime.Days, gameTime.Hours, gameTime.Minutes, gameTime.Seconds); // remove fractional seconds
            // TODO: add logic to format without hours if 0 --> custom format string?

            HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetPlayersToNotify(matchData, notifySet);

                _chat.SendSetMessage(notifySet, $"{killed.Name} kb {killer.Name}"); // TODO: add assist logic, but that belongs in the TeamVersusStats module, so need a callback to fire and this probably need to add interface method to this for sending the notification?

                if (killedPlayerSlot.Lives > 0)
                {
                    // The slot still has lives, allow the player to ship change (after death) for a limited amount of time.
                    killedPlayerSlot.AllowShipChangeCutoff = DateTime.UtcNow + TimeSpan.FromSeconds(5); // TODO: make this configurable. Also, maybe limit how many change a player can make?

                    _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killed.Name} has {killedPlayerSlot.Lives} {(killedPlayerSlot.Lives > 1 ? "lives" : "life")} remaining [{gameTime:g}]");
                }
                else
                {
                    // This was the last life of the slot.
                    killedPlayerSlot.Status = PlayerSlotStatus.KnockedOut;

                    _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killed.Name} is OUT! [{gameTime:g}]");

                    // The player needs to be moved to spec, but if done immediately any of lingering weapons fire from the player will be removed.
                    // We want to wait a short time to allow for a double-kill (though shorter than respawn time), before moving the player to spec and checking for match completion.
                    _mainloopTimer.SetTimer(MainloopTimer_ProcessKnockOut, (int)matchData.Configuration.WinConditionDelay.TotalMilliseconds, Timeout.Infinite, killedPlayerSlot, matchData);
                }

                //_chat.SendSetMessage(notifySet, $"Score: {}-{}")
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
            }
            

            bool MainloopTimer_ProcessKnockOut(PlayerSlot slot)
            {
                if (slot.Player is not null && slot.Player.Ship != ShipType.Spec)
                {
                    _game.SetShip(slot.Player, ShipType.Spec);
                }

                CheckForMatchCompletion(slot.MatchData);
                return false;
            }
        }

        // This is called synchronously when the Game module sets a player's ship/freq.
        private void Callback_PreShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.AssignedSlot;
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
                        _chat.SendSetMessage(players, $"{slot.Player.Name} changed to a {slot.Player.Ship}");
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
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
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

            PlayerSlot slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            if (slot.MatchData.Status == MatchStatus.InProgress
                && slot.Status == PlayerSlotStatus.Playing
                && newShip == ShipType.Spec)
            {
                SetSlotInactive(slot, SlotNotActiveReason.ChangedToSpec);
            }
        }

        private void Callback_PlayerPositionPacket(Player player, in C2S_PositionPacket positionPacket, bool hasExtraPositionData)
        {
            if (player.Ship == ShipType.Spec)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.AssignedSlot;
            if (slot is null || slot.Status != PlayerSlotStatus.Playing)
                return;

            if (hasExtraPositionData)
            {
                // Keep track of the items for the slot.
                slot.Bursts = positionPacket.Extra.Bursts;
                slot.Repels = positionPacket.Extra.Repels;
                slot.Thors = positionPacket.Extra.Thors;
                slot.Bricks = positionPacket.Extra.Bricks;
                slot.Decoys = positionPacket.Extra.Decoys;
                slot.Rockets = positionPacket.Extra.Rockets;
                slot.Portals = positionPacket.Extra.Portals;
            }

            if (positionPacket.Weapon.Type != WeaponCodes.Null // Note: bricks are not position packet weapons, therefore handled separately with Callback_BricksPlaced
                && slot.AllowShipChangeCutoff is not null)
            {
                // The player has engaged.
                slot.AllowShipChangeCutoff = null;
            }

            MatchData matchData = slot.MatchData;
            string playAreaRegion = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].PlayAreaMapRegion;
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

                    foreach (var region in _mapData.RegionsAt(player.Arena, (short)(positionPacket.X >> 4), (short)(positionPacket.Y >> 4)))
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
                            QueueCheckForMatchCompletion(matchData, (int)matchData.Configuration.WinConditionDelay.TotalMilliseconds);
                        }
                    }
                }
            }

            slot.IsFullEnergy =
                _arenaDataDictionary.TryGetValue(player.Arena, out ArenaData arenaData)
                && positionPacket.Energy >= arenaData.ShipSettings[(int)player.Ship].MaximumEnergy;

            if (slot.SubPlayer is not null && slot.IsFullEnergy)
            {
                // Another player is waiting to sub in. Try to sub the other player in.
                SubSlot(slot);
            }
        }

        private void Callback_BricksPlaced(Arena arena, Player player, IReadOnlyList<BrickData> bricks)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.AssignedSlot;
            if (slot is null || slot.Status != PlayerSlotStatus.Playing)
                return;

            if (slot.AllowShipChangeCutoff is not null)
            {
                // The player has engaged.
                slot.AllowShipChangeCutoff = null;
            }
        }

        private void Callback_Spawn(Player player, SpawnCallback.SpawnReason reasons)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.AssignedSlot;
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
            Description = "Loads (or reloads) a match type from configuration.")]
        private void Command_loadmatchtype(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // TODO:

            // End any existing match of this type.

            // Remember to use _configManager.OpenConfigFile on a worker thread.
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<match type>",
            Description = "Removes a match type.")]
        private void Command_unloadmatchtype(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // TODO: 

            // End any existing match of this type.

            // Remove the match type.

            // If the queue that the match used doesn't have any other match types, then remove it too.
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "For a player to request to be subbed out of their current match.")]
        private void Command_requestsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.AssignedSlot;
            if (slot is null)
                return;

            if (slot.IsSubRequested)
            {
                _chat.SendMessage(player, $"You've already requested to be subbed out.");
                return;
            }

            slot.IsSubRequested = true;

            if (slot.AvailableSubSlotNode.List is null)
            {
                _availableSubSlots.AddLast(slot.AvailableSubSlotNode);
            }

            _chat.SendArenaMessage(player.Arena, $"{player.Name} has requested to be subbed out.");
            SendSubAvailablityNotificationToQueuedPlayers(slot.MatchData);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<queue name>]",
            Description = 
            "Searches for the next available slot in any ongoing matches and substitutes the player into the slot.\n" +
            "When matched with a slot to sub in for, the switch may not happen immediately." +
            "First, the player may need to be moved to the proper arena." +
            "Also, the slot may currently contain an active player (that requested to be subbed out), " +
            "in which case the active player needs to get to full energy to become eligible to be switched out.")]
        private void Command_sub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // TODO: maybe the sub command should be in the MatchMakingQueues module, and this method is an advisor method?

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, $"You must be in spec to use ?{CommandNames.Sub}.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
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

            // TODO:
            //if (_matchmakingQueues.IsQueued(player))
            //{
            //    _chat.SendMessage(player, $"You must be queued as a solo to use ?{CommandNames.Sub}");
            //    return;
            //}

            // Find the next available slot, if one exists.
            PlayerSlot subSlot = null;

            foreach (PlayerSlot slot in _availableSubSlots)
            {
                if (slot.CanBeSubbed
                    && slot.SubPlayer is null // no sub in progress
                    && (parameters.IsWhiteSpace() || parameters.Equals(slot.MatchData.Configuration.QueueName, StringComparison.OrdinalIgnoreCase))
                    && _queueDictionary.TryGetValue(slot.MatchData.Configuration.QueueName, out TeamVersusMatchmakingQueue queue)
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
            Arena arena = _arenaManager.FindArena(matchData.ArenaName);
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

            Player player = slot.SubPlayer;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                // No ongoing sub for the slot.
                return;
            }

            MatchData matchData = slot.MatchData;
            Arena arena = player.Arena;
            if (arena is null 
                || !string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase)
                || !playerData.HasEnteredArena)
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
                _chat.SendMessage(slot.SubPlayer, "You will be subbed in when the active player gets to full energy. Please stand by.");
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
            matchData.ParticipationList.Insert(subInIndex, new PlayerParticipationRecord(player.Name, true, false));

            // Clear the available/pending sub.
            slot.AvailableSubSlotNode.List?.Remove(slot.AvailableSubSlotNode);
            slot.SubPlayer = null;
            playerData.SubSlot = null;

            // Do the actual sub in.
            string subOutPlayerName = slot.PlayerName;
            AssignSlot(slot, player);
            SetShipAndFreq(slot, true, null);

            TeamVersusMatchPlayerSubbedCallback.Fire(arena, slot, subOutPlayerName);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = $"Cancels a ?{CommandNames.Sub} attempt that's in progress.\n" +
            "Use this if you no longer want to wait to be subbed in.")]
        private void Command_cancelsub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
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

            Player player = slot.SubPlayer;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
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
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                return;
            }

            PlayerSlot slot = playerData.AssignedSlot;
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

            PlayerSlot slot = playerData.AssignedSlot;
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
            Description = 
            "Request that teams be randomized and the game restarted, needs a majority vote.\n" +
            "Using this ignores player groups, all players in the match will be randomized.")]
        private void Command_randomize(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requests to re-randomize the teams and restart the game. To agree, type: ?randomize");
            //_chat.SendMessage(, $"{player.Name} agreed to ?randomize. ({forVotes}/5)");
            //_chat.SendMessage(, $"The vote to ?randomize has expired.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = 
            "Request that the game be ended as a loss for the team, " +
            "if all remaining players on a team agree (type the command), " +
            "the game will end as a loss to that team without having to be killed out.")]
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
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot playerSlot = playerData.AssignedSlot;
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

            if (playerSlot.AllowShipChangeCutoff is not null && playerSlot.AllowShipChangeCutoff < DateTime.UtcNow)
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

        #endregion

        #region IMatchmakingQueueAdvisor

        string IMatchmakingQueueAdvisor.GetDefaultQueue(Arena arena)
        {
            if (!_arenaBaseDataDictionary.TryGetValue(arena.BaseName, out ArenaBaseData arenaBaseData))
                return null;

            return arenaBaseData.DefaultQueueName;
        }

        bool IMatchmakingQueueAdvisor.TryGetCurrentMatchInfo(string playerName, StringBuilder matchInfo)
        {
            if (!_playerSlotDictionary.TryGetValue(playerName, out PlayerSlot slot))
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

        private bool LoadConfiguration()
        {
            ConfigHandle ch = _configManager.OpenConfigFile(null, ConfigurationFileName);
            if (ch is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), $"Error opening {ConfigurationFileName}.");
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
                    if (matchConfiguration is null)
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

                    if (!_arenaBaseDataDictionary.TryGetValue(matchConfiguration.ArenaBaseName, out ArenaBaseData arenaBaseData))
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
                if (timeLimit is not null)
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
            if (queue is null)
                return false;

            // Most often, a queue will be used by a single match type. However, multiple match types can use the same queue.
            // An example of where this may be used is if there are multiple maps that can be used for a particular game type.
            // Here, we randomize which match type to start searching.
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
                // Found an available location for a game to be played in. Next, try to find players.
                //

                if (!queue.GetParticipants(matchData))
                    continue;

                //
                // Mark the players as playing.
                //

                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            players.Add(slot.Player);
                        }
                    }

                    _matchmakingQueues.SetPlaying(players);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }

                //
                // Start the game.
                //

                // Reserve the match.
                matchData.Status = MatchStatus.Initializing;

                // Try to find the arena.
                Arena arena = _arenaManager.FindArena(matchData.ArenaName); // This will only find the arena if it already exists and is running.

                foreach (Team team in matchData.Teams)
                {
                    foreach (PlayerSlot slot in team.Slots)
                    {
                        Player player = slot.Player;

                        AssignSlot(slot, player);

                        slot.Status = PlayerSlotStatus.Waiting;

                        if (arena is null || player.Arena != arena)
                        {
                            _arenaManager.SendToArena(player, matchData.ArenaName, 0, 0);
                        }
                    }
                }

                QueueMatchInitialzation(matchData);
                return true;
            }

            return false;

            bool TryGetAvailableMatch(MatchConfiguration matchConfiguration, out MatchData matchData)
            {
                if (!_arenaBaseDataDictionary.TryGetValue(matchConfiguration.ArenaBaseName, out ArenaBaseData arenaBaseData))
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
                            _matchDataDictionary.Add(matchIdentifier, matchData);
                            return true;
                        }

                        if (matchData.Status == MatchStatus.None)
                            return true;
                    }
                }

                // no availablity
                matchData = null;
                return false;
            }
        }

        private void AssignSlot(PlayerSlot slot, Player player)
        {
            if (slot is null || player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
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
                        _chat.SendSetMessage(players, $"{player.Name} in for {slot.PlayerName} [Lives: {slot.Lives}]");
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
            _playerSlotDictionary.Add(player.Name, slot);
            slot.PlayerName = player.Name;
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
                if (slot.Player.TryGetExtraData(_pdKey, out PlayerData playerData))
                {
                    playerData.AssignedSlot = null;
                }

                slot.Player = null;
            }
        }

        private void SetSlotInactive(PlayerSlot slot, SlotNotActiveReason reason)
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

                        if (reason == SlotNotActiveReason.ChangedToSpec)
                        {
                            _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode. The slot is now available for ?{CommandNames.Sub}.");
                        }
                        else if (reason == SlotNotActiveReason.LeftArena)
                        {
                            _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena. The slot is now available for ?{CommandNames.Sub}.");
                        }

                        SendSubAvailablityNotificationToQueuedPlayers(matchData);
                    }
                }
                else
                {
                    // The slot will become available to sub if the player doesn't return in time.
                    if (reason == SlotNotActiveReason.ChangedToSpec)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode and has {matchData.Configuration.AllowSubAfter.TotalSeconds} seconds to return before the slot is opened to ?{CommandNames.Sub}. [Lagouts: {slot.LagOuts}]");
                    }
                    else if (reason == SlotNotActiveReason.LeftArena)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena and has {matchData.Configuration.AllowSubAfter.TotalSeconds} seconds to return before the slot is opened to ?{CommandNames.Sub}. [Lagouts: {slot.LagOuts}]");
                    }

                    _mainloopTimer.SetTimer(MainloopTimer_ProcessInactiveSlot, (int)matchData.Configuration.AllowSubAfter.TotalMilliseconds, Timeout.Infinite, slot, slot);
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
                        QueueCheckForMatchCompletion(matchData, 15000); // TODO: configuration setting (remember to change the chat notification too)
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        private void SendSubAvailablityNotificationToQueuedPlayers(MatchData matchData)
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

            SendSubAvailablityNotificationToQueuedPlayers(slot.MatchData);

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
            Arena arena = _arenaManager.FindArena(matchData.ArenaName);
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

        private void QueueMatchInitialzation(MatchData matchData)
        {
            if (matchData is null)
                return;

            _mainloop.QueueMainWorkItem(MainloopWork_DoMatchInitialization, matchData);

            void MainloopWork_DoMatchInitialization(MatchData matchData)
            {
                if (matchData.Status == MatchStatus.Initializing)
                {
                    Arena arena = _arenaManager.FindArena(matchData.ArenaName);
                    if (arena is null)
                        return;

                    // Check that all players are in the arena.
                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            if (slot.Status != PlayerSlotStatus.Waiting
                                || slot.Player is null
                                || slot.Player.Arena != arena
                                || !slot.Player.TryGetExtraData(_pdKey, out PlayerData playerData)
                                || !playerData.HasEnteredArena)
                            {
                                // Can't start
                                return;
                            }
                        }
                    }

                    // Start the match.
                    foreach(Team team in matchData.Teams)
                    {
                        MapCoordinate[] startLocations = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[team.TeamIdx];
                        int startLocationIdx = startLocations.Length == 1 ? 0 : _prng.Number(0, startLocations.Length - 1);
                        MapCoordinate startLocation = startLocations[startLocationIdx];

                        foreach (PlayerSlot playerSlot in team.Slots)
                        {
                            Player player = playerSlot.Player;
                            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                                continue;

                            playerSlot.Lives = matchData.Configuration.LivesPerPlayer;
                            playerSlot.LagOuts = 0;

                            SetShipAndFreq(playerSlot, false, startLocation);
                        }
                    }

                    matchData.Status = MatchStatus.InProgress;
                    matchData.Started = DateTime.UtcNow;

                    // Fire a callback to notify the stats module that the match has started.
                    TeamVersusMatchStartedCallback.Fire(arena, matchData);
                }
            }
        }

        private void SetShipAndFreq(PlayerSlot slot, bool isRefill, MapCoordinate? startLocation)
        {
            Player player = slot.Player;
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
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
                Player player = slot.Player;
                if (player is null)
                    return;

                Arena arena = player.Arena;
                if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData arenaData))
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

        private void QueueCheckForMatchCompletion(MatchData matchData, int timerDelayMs)
        {
            if (matchData is null)
                return;

            _mainloopTimer.ClearTimer<MatchData>(CheckForMatchCompletion, matchData);
            _mainloopTimer.SetTimer(CheckForMatchCompletion, timerDelayMs, Timeout.Infinite, matchData, matchData);
        }

        private bool CheckForMatchCompletion(MatchData matchData)
        {
            if (matchData is null)
                return false;

            bool isPlayAreaCheckEnabled = !string.IsNullOrWhiteSpace(matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].PlayAreaMapRegion);
            int remainingTeams = 0;
            Team lastRemainingTeam = null;

            foreach(Team team in matchData.Teams)
            {
                bool isTeamKnockedOut = true;

                foreach(PlayerSlot slot in team.Slots)
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

            return false;
        }

        private void EndMatch(MatchData matchData, MatchEndReason reason, Team winnerTeam)
        {
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

                if (winnerTeam != null)
                {
                    scoreBuilder.Append($" Freq {winnerTeam.Freq}");
                }
                else
                {
                    scoreBuilder.Append(" DRAW");
                }

                _chat.SendSetMessage(notifySet, $"Final {matchData.MatchIdentifier.MatchType} Score: {scoreBuilder}");
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
                _objectPoolManager.StringBuilderPool.Return(scoreBuilder);
            }

            Arena arena = _arenaManager.FindArena(matchData.ArenaName); // Note: this can be null (the arena can be destroyed before the match ends)

            // Fire a callback to notify the stats module that the match has ended.
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
            // Clear the 'playing' state of all players that were associated with the now completed match.
            //

            foreach (PlayerParticipationRecord record in matchData.ParticipationList)
            {
                if (record.LeftWithoutSub)
                    continue;

                _playerSlotDictionary.Remove(record.PlayerName);
                _mainloop.QueueMainWorkItem(MainloopWork_UnsetPlayingWithRequeue, record.PlayerName);
            }

            foreach (PlayerParticipationRecord record in matchData.ParticipationList)
            {
                if (!record.LeftWithoutSub)
                    continue;

                _playerSlotDictionary.Remove(record.PlayerName);
                _mainloop.QueueMainWorkItem(MainloopWork_UnsetPlayingWithoutRequeue, record.PlayerName);
            }

            //
            // Clear match data.
            //

            matchData.ParticipationList.Clear();

            // Clear the team data.
            foreach(Team team in matchData.Teams)
            {
                // Slots
                foreach (PlayerSlot slot in team.Slots)
                {
                    UnassignSlot(slot); // this clears slot.PlayerName and slot.Player
                    slot.Status = PlayerSlotStatus.None;
                    slot.InactiveTimestamp = null;
                    slot.LagOuts = 0;
                    slot.AvailableSubSlotNode.List?.Remove(slot.AvailableSubSlotNode);
                    slot.IsSubRequested = false;
                    // Note: slot.SubPlayer should already be null because we would have called CancelSubInProgress
                    slot.IsFullEnergy = false;
                    slot.IsInPlayArea = false;
                    slot.AllowShipChangeCutoff = null;
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

            matchData.Started = null;

            // Set the status. This makes it available to host a new game.
            //matchData.Status = MatchStatus.Complete; // TODO: add a timer to have a delay between games?
            matchData.Status = MatchStatus.None;

            void MainloopWork_UnsetPlayingWithRequeue(string playerName)
            {
                _matchmakingQueues.UnsetPlaying(playerName, true);
            }

            void MainloopWork_UnsetPlayingWithoutRequeue(string playerName)
            {
                _matchmakingQueues.UnsetPlaying(playerName, false);
            }
        }

        #region Helper types

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

        private enum MatchStatus
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
            IMatchConfiguration IMatchData.Configuration { get; }

            public string ArenaName { get; }

            public Arena Arena { get; set; }

            /// <summary>
            /// The status of the match.
            /// Used to tell if the match is in use (reserved for a game), and if so, what stage it's in.
            /// </summary>
            public MatchStatus Status;

            /// <inheritdoc cref="IMatchData.Teams"/>
            public readonly Team[] Teams;
            private readonly ReadOnlyCollection<ITeam> _readOnlyTeams;
            ReadOnlyCollection<ITeam> IMatchData.Teams => _readOnlyTeams;

            public DateTime? Started { get; set; }

            /// <summary>
            /// List for keeping track of the players that have participated in the match so that they can be unset from the 'Playing' state when the match ends.
            /// </summary>
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
            IMatchData ITeam.MatchData  => MatchData;

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

            public string PlayerName { get; set; }

            public Player Player { get; set; }

            /// <summary>
            /// The time the slot became inactive (player changed to spec or left the arena).
            /// This will allow us to figure out when the slot can be given to a substibute player.
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
                            && (DateTime.UtcNow - InactiveTimestamp.Value) >= MatchData.Configuration.AllowSubAfter
                        )
                    )
                );

            /// <summary>
            /// The player that is in the process of being subbed into the slot.
            /// </summary>
            public Player SubPlayer;

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
            /// The cutoff timestamp that the player can ship change (after death or at the start of a match).
            /// </summary>
            public DateTime? AllowShipChangeCutoff;

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

        private class PlayerData
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
            public PlayerSlot AssignedSlot = null;

            /// <summary>
            /// The slot the player is in the process of being subbed into.
            /// </summary>
            public PlayerSlot SubSlot = null;

            /// <summary>
            /// The player's next ship.
            /// Used to spawn the player in the ship of their choosing (after death or in the next match).
            /// </summary>
            public ShipType? NextShip = null;

            /// <summary>
            /// Whether the player has fully entered the arena (including sending a position packet).
            /// This tells us if the player is able to be placed into a ship.
            /// </summary>
            public bool HasEnteredArena = false;

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
        }

        private class PlayerDataPooledObjectPolicy : IPooledObjectPolicy<PlayerData>
        {
            public PlayerData Create()
            {
                return new PlayerData();
            }

            public bool Return(PlayerData obj)
            {
                if (obj is null)
                    return false;

                obj.AssignedSlot = null;
                obj.NextShip = null;
                obj.HasEnteredArena = false;
                obj.IsReturning = false;

                return true;
            }
        }

        private struct ShipSettings
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
            public string DefaultQueueName;
        }

        /// <summary>
        /// Data for each arena that has a team versus match type
        /// </summary>
        private class ArenaData
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor> IFreqManagerEnforcerAdvisorToken;
            public ShipSettings[] ShipSettings = new ShipSettings[8];
        }

        private class ArenaDataPooledObjectPolicy : IPooledObjectPolicy<ArenaData>
        {
            public ArenaData Create()
            {
                return new ArenaData();
            }

            public bool Return(ArenaData obj)
            {
                if (obj is null)
                    return false;

                obj.IFreqManagerEnforcerAdvisorToken = null;

                return true;
            }
        }

        /// <summary>
        /// A type that wraps either a <see cref="Core.Player"/> or <see cref="IPlayerGroup"/>.
        /// </summary>
        private readonly record struct QueuedPlayerOrGroup
        {
            public QueuedPlayerOrGroup(Player player, DateTime timestamp)
            {
                Player = player ?? throw new ArgumentNullException(nameof(player));
                Group = null;
                Timestamp = timestamp;
            }

            public QueuedPlayerOrGroup(IPlayerGroup group, DateTime timestamp)
            {
                Player = null;
                Group = group ?? throw new ArgumentNullException(nameof(group));
                Timestamp = timestamp;
            }

            public Player Player { get; }
            public IPlayerGroup Group { get; }
            public DateTime Timestamp { get; }
        }

        private class TeamVersusMatchmakingQueue : IMatchmakingQueue
        {
            private readonly LinkedList<QueuedPlayerOrGroup> _queue = new(); // TODO: object pooling of LinkedListNode<QueuedPlayerOrGroup>

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

            #region Add

            public bool Add(Player player, DateTime timestamp)
            {
                Add(new LinkedListNode<QueuedPlayerOrGroup>(new(player, timestamp)));
                return true;
            }

            public bool Add(IPlayerGroup group, DateTime timestamp)
            {
                Add(new LinkedListNode<QueuedPlayerOrGroup>(new(group, timestamp)));
                return true;
            }

            private void Add(LinkedListNode<QueuedPlayerOrGroup> item)
            {
                if (_queue.Count == 0 || _queue.Last.ValueRef.Timestamp <= item.ValueRef.Timestamp)
                {
                    _queue.AddLast(item);
                }
                else
                {
                    var node = _queue.First;
                    while (node is not null && node.ValueRef.Timestamp <= item.ValueRef.Timestamp)
                    {
                        node = node.Next;
                    }

                    if (node is not null)
                    {
                        _queue.AddBefore(node, item);
                    }
                    else
                    {
                        _queue.AddLast(item);
                    }
                }
            }

            #endregion

            #region Remove

            public bool Remove(Player player)
            {
                LinkedListNode<QueuedPlayerOrGroup> node = _queue.First;
                while (node is not null)
                {
                    if (node.ValueRef.Player == player)
                    {
                        _queue.Remove(node);
                        return true;
                    }

                    node = node.Next;
                }

                return false;
            }

            public bool Remove(IPlayerGroup group)
            {
                LinkedListNode<QueuedPlayerOrGroup> node = _queue.First;
                while (node is not null)
                {
                    if (node.ValueRef.Group == group)
                    {
                        _queue.Remove(node);
                        return true;
                    }

                    node = node.Next;
                }

                return false;
            }

            #endregion

            public bool ContainsSoloPlayer(Player player)
            {
                foreach (QueuedPlayerOrGroup pog in _queue)
                {
                    if (pog.Player == player)
                        return true;
                }

                return false;
            }

            public void GetQueued(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups)
            {
                foreach (QueuedPlayerOrGroup pog in _queue)
                {
                    if (pog.Player is not null)
                        soloPlayers?.Add(pog.Player);
                    else if (pog.Group is not null)
                        groups?.Add(pog.Group);
                }
            }

            public bool GetParticipants(MatchData matchData)
            {
                if (matchData is null)
                    return false;

                int playersPerTeam = matchData.Configuration.PlayersPerTeam;

                // TODO: logic is simplified when only allowing groups of the exact size, add support for other group sizes later
                if (playersPerTeam != Options.MinGroupSize || playersPerTeam != Options.MaxGroupSize)
                {
                    return false;
                }

                List<LinkedListNode<QueuedPlayerOrGroup>> pending = new(); // TODO: object pooling
                List<LinkedListNode<QueuedPlayerOrGroup>> pendingSolo = new(); // TODO: object pooling

                try
                {
                    foreach (Team team in matchData.Teams)
                    {
                        LinkedListNode<QueuedPlayerOrGroup> node = _queue.First;
                        if (node is null)
                            break; // Cannot fill the team.

                        ref QueuedPlayerOrGroup pog = ref node.ValueRef;

                        if (pog.Group is not null)
                        {
                            // Got a group, which fills the team.
                            Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                            for (int slotIdx = 0; slotIdx < playersPerTeam; slotIdx++)
                                team.Slots[slotIdx].Player = pog.Group.Members[slotIdx];

                            team.IsPremade = true;
                            _queue.Remove(node);
                            pending.Add(node);
                            continue; // Filled the team with a group.
                        }
                        else if (pog.Player is not null)
                        {
                            // Got a solo player, check if there are enough solo players to fill the team.
                            pendingSolo.Add(node);

                            while (pendingSolo.Count < playersPerTeam
                                && (node = node.Next) is not null)
                            {
                                pog = ref node.ValueRef;
                                if (pog.Player is not null)
                                {
                                    pendingSolo.Add(node);
                                }
                            }

                            if (pendingSolo.Count == playersPerTeam)
                            {
                                // Found enough solo players to fill the team.
                                int slotIdx = 0;
                                foreach (LinkedListNode<QueuedPlayerOrGroup> soloNode in pendingSolo)
                                {
                                    pog = ref soloNode.ValueRef;
                                    team.Slots[slotIdx++].Player = pog.Player;
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
                                while (node is not null)
                                {
                                    pog = ref node.ValueRef;
                                    if (pog.Group is not null)
                                        break;

                                    node = node.Next;
                                }

                                if (node is not null && pog.Group is not null)
                                {
                                    // Got a group, which fills the team.
                                    Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                                    for (int slotIdx = 0; slotIdx < playersPerTeam; slotIdx++)
                                        team.Slots[slotIdx].Player = pog.Group.Members[slotIdx];

                                    team.IsPremade = true;
                                    _queue.Remove(node);
                                    pending.Add(node);
                                    continue; // Filled the team with a group.
                                }

                                break; // Cannot fill the team.
                            }
                        }
                    }

                    // TODO: configuration setting to consider a successful matching to have a certain minimum # of filled teams? (e.g. a 2 player per team FFA match)
                    // TODO: configuration setting to allow a partially filled team if it has a configured minimum # of players? (e.g. a battle royale style game that couldn't completely fill a team but has enough teams total)

                    bool success = true;
                    foreach (Team team in matchData.Teams)
                    {
                        foreach (PlayerSlot slot in team.Slots)
                        {
                            if (slot.Player is null)
                            {
                                success = false;
                                break;
                            }
                        }
                    }

                    if (success)
                    {
                        bool hasPremadeTeam = false;
                        foreach (Team team in matchData.Teams)
                        {
                            if (team.IsPremade)
                            {
                                hasPremadeTeam = true;
                                break;
                            }
                        }

                        if (!hasPremadeTeam)
                        {
                            // All the participants were solo players (no groups).
                            // TODO: randomize the teams
                        }

                        // Add participation records.
                        foreach (LinkedListNode<QueuedPlayerOrGroup> node in pending)
                        {
                            ref QueuedPlayerOrGroup pog = ref node.ValueRef;
                            if (pog.Player is not null)
                            {
                                matchData.ParticipationList.Add(new PlayerParticipationRecord(pog.Player.Name, false, false));
                            }
                            else if (pog.Group is not null)
                            {
                                foreach (Player player in pog.Group.Members)
                                {
                                    matchData.ParticipationList.Add(new PlayerParticipationRecord(player.Name, false, false));
                                }
                            }
                        }

                        return true;
                    }
                    else
                    {
                        // Unable to fill the teams.
                        // Add all the pending nodes back into the queue in their original order.
                        for (int i = pending.Count - 1; i >= 0; i--)
                        {
                            LinkedListNode<QueuedPlayerOrGroup> node = pending[i];
                            pending.RemoveAt(i);
                            _queue.AddFirst(node);
                        }

                        foreach (Team team in matchData.Teams)
                        {
                            foreach (PlayerSlot slot in team.Slots)
                            {
                                slot.Player = null;
                            }
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
        }

        private enum SlotNotActiveReason
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
