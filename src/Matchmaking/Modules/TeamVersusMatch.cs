﻿using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Packets.Game;
using SS.Utilities;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public class TeamVersusMatch : IModule, IMatchmakingQueueAdvisor, IFreqManagerEnforcerAdvisor
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

        /// <summary>
        /// Slots that are available for substitute players.
        /// </summary>
        private readonly LinkedList<PlayerSlot> _availableSubSlots = new();

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new();

        private readonly ObjectPool<ArenaData> _arenaDataObjectPool = new NonTransientObjectPool<ArenaData>(new ArenaDataPooledObjectPolicy());

        /// <summary>
        /// Set of slots that can be subbed.
        /// </summary>
        //private HashSet<PlayerSlot> _slotsToSubSet = new();

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
            INetwork network,
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

            _network.AddPacket(C2SPacketType.Position, Packet_Position);

            _iMatchmakingQueueAdvisorToken = broker.RegisterAdvisor<IMatchmakingQueueAdvisor>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _iMatchmakingQueueAdvisorToken);

            _network.RemovePacket(C2SPacketType.Position, Packet_Position);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchmakingQueueChangedCallback.Unregister(broker, Callback_MatchmakingQueueChanged);

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvsor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            // TOOD: When in a match, allow a player to do a normal ship change instead of using ?sc
            // If during a period they can ship change, then allow all ships.
            // If during a period they cannot ship change, but have additional lives, set their next ship.
            return ShipMask.None;
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
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return false;

            PlayerSlot playerSlot = playerData.PlayerSlot;
            if (playerSlot == null)
                return false; // not in match

            //if(playerSlot.Player == player)
            // Manual changing of ship/freq is not allowed.

            return false;
        }

        #endregion

        #region Packet handlers

        private void Packet_Position(Player player, byte[] data, int length)
        {
            if (length != C2S_PositionPacket.LengthWithExtra)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot playerSlot = playerData.PlayerSlot;
            if (playerSlot == null || playerSlot.Status != PlayerSlotStatus.Playing)
                return;

            ref C2S_PositionPacket pos = ref MemoryMarshal.AsRef<C2S_PositionPacket>(new Span<byte>(data, 0, C2S_PositionPacket.LengthWithExtra));

            // Keep track of the items for the slot.
            playerSlot.Bursts = pos.Extra.Bursts;
            playerSlot.Repels = pos.Extra.Repels;
            playerSlot.Thors = pos.Extra.Thors;
            playerSlot.Bricks = pos.Extra.Bricks;
            playerSlot.Decoys = pos.Extra.Decoys;
            playerSlot.Rockets = pos.Extra.Rockets;
            playerSlot.Portals = pos.Extra.Portals;

            if (pos.Weapon.Type != WeaponCodes.Null
                && playerSlot.AllowShipChangeCutoff != null)
            {
                // The player has engaged.
                playerSlot.AllowShipChangeCutoff = null;
            }
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
                    };
                }

                KillCallback.Register(arena, Callback_Kill);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);

                _commandManager.AddCommand(CommandNames.RequestSub, Command_requestsub, arena); // allow a player request for their slot to be subbed out
                _commandManager.AddCommand(CommandNames.Sub, Command_sub, arena); // for a player to confirm that they agree to sub in
                _commandManager.AddCommand(CommandNames.Return, Command_return, arena); // return to the game after being spec'd or disconnected
                _commandManager.AddCommand(CommandNames.Restart, Command_restart, arena); // request that the game be restarted, needs a majority vote
                _commandManager.AddCommand(CommandNames.Randomize, Command_randomize, arena); // request that teams be randomized and restarted, needs a majority vote --> using this ignores player groups, all players in the match will be randomized
                _commandManager.AddCommand(CommandNames.End, Command_end, arena); // request that the game be ended as a loss for the team, if all remaining players on a team agree (type the command), the game will end as a loss to that team without having to be killed out.
                _commandManager.AddCommand(CommandNames.Draw, Command_draw, arena); // request that the game be ended as a draw by agreement (no winner), needs a majority vote across all remaining players
                _commandManager.AddCommand(CommandNames.ShipChange, Command_sc, arena); // request a ship change. It will be allowed after death.  Otherwise, the sets the next ship to use upon death.

                arenaData.IFreqManagerEnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData arenaData))
                    return;

                if (!arena.UnregisterAdvisor(ref arenaData.IFreqManagerEnforcerAdvisorToken))
                    return;

                KillCallback.Unregister(arena, Callback_Kill);
                ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);

                _commandManager.RemoveCommand(CommandNames.RequestSub, Command_requestsub, arena);
                _commandManager.RemoveCommand(CommandNames.Sub, Command_sub, arena);
                _commandManager.RemoveCommand(CommandNames.Return, Command_return, arena);
                _commandManager.RemoveCommand(CommandNames.Restart, Command_restart, arena);
                _commandManager.RemoveCommand(CommandNames.Randomize, Command_randomize, arena);
                _commandManager.RemoveCommand(CommandNames.End, Command_end, arena);
                _commandManager.RemoveCommand(CommandNames.Draw, Command_draw, arena);
                _commandManager.RemoveCommand(CommandNames.ShipChange, Command_sc, arena);

                // TODO: make sure all matches in the arena are cleared out
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Connect)
            {
                if (_playerSlotDictionary.TryGetValue(player.Name, out PlayerSlot playerSlot)
                    && string.Equals(playerSlot.PlayerName, player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                        return;

                    playerData.PlayerSlot = playerSlot;
                    playerSlot.Player = player;

                    // TODO: send a message to the player that they're still in the match

                    // TODO: automatically move the player to the proper arena
                    // Can't use IArenaManager.SendToArena() here because this event happens before the player is in the proper state to allow that.
                    // But, a hacky way to do it is to overwrite:
                    //player.ConnectAs = playerSlot.MatchData.ArenaName; // if it were public...
                    // which will tell the ArenaPlaceMultiPub module to send them to the proper arena when the client sends the Go packet.
                    // maybe add
                    // player.ConnectAsOverride? and remember to clear it when the player enters the arena?
                }
            }
            //else if (action == PlayerAction.EnterArena) // Purposely using EnterGame instead
            //{
            //}
            else if (action == PlayerAction.EnterGame)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                playerData.HasEnteredArena = true;

                PlayerSlot slot = playerData.PlayerSlot;
                if (slot != null)
                {
                    MatchData matchData = playerData.PlayerSlot.MatchData;

                    if (matchData.Status == MatchStatus.Initializing
                        && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        // The player entered an arena for a match that's starting up.
                        QueueMatchInitialzation(playerData.PlayerSlot.MatchData);
                    }
                    else if (playerData.IsReturning
                        && string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                    {
                        // The player re-entered an arena for a match they're trying to ?return to.
                        playerData.IsReturning = false;
                        ReturnToMatch(player, playerData);
                    }
                }
                else if (playerData.SubSlot != null
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

                if (playerData.SubSlot != null
                    && string.Equals(arena.Name, playerData.SubSlot.MatchData.ArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    // The player left the arena before entering the game, and therefore did not complete subbing into the match.
                    CancelInProgressSub(playerData.SubSlot, false);
                }

                PlayerSlot slot = playerData.PlayerSlot;
                if (slot != null
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

                if (playerData.SubSlot != null)
                {
                    // The player disconnected from the server before being able to complete subbing into the match.
                    CancelInProgressSub(playerData.SubSlot, false);
                }

                PlayerSlot slot = playerData.PlayerSlot;
                if (slot == null)
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

            PlayerSlot killedPlayerSlot = killedPlayerData.PlayerSlot;
            if (killedPlayerSlot == null)
                return;

            killedPlayerSlot.Lives--;

            if (killedPlayerSlot.Lives > 0)
            {
                // The slot still has lives, allow the player to ship change using the ?sc command.
                killedPlayerSlot.AllowShipChangeCutoff = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            }
            else
            {
                // This was the last life of the slot.
                killedPlayerSlot.Status = PlayerSlotStatus.KnockedOut;

                // The player needs to be moved to spec, but if done immediately any of lingering weapons fire from the player will be removed.
                // We want to wait a short time to allow for a double-kill (though shorter than respawn time), before moving the player to spec and checking for match completion.
                MatchData matchData = killedPlayerSlot.MatchData;
                _mainloopTimer.SetTimer(ProcessKnockOut, (int)matchData.Configuration.WinConditionDelay.TotalMilliseconds, Timeout.Infinite, killedPlayerSlot, matchData);
            }

            bool ProcessKnockOut(PlayerSlot slot)
            {
                if (slot.Player != null && slot.Player.Ship != ShipType.Spec)
                {
                    _game.SetShip(slot.Player, ShipType.Spec);
                }

                CheckForMatchCompletion(slot.MatchData);
                return false;
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot slot = playerData.PlayerSlot;
            if (slot == null)
                return;

            MatchData matchData = slot.MatchData;
            if (matchData.Status == MatchStatus.InProgress)
            {
                if (slot.Status == PlayerSlotStatus.Playing)
                {
                    if (newShip == ShipType.Spec)
                    {
                        SetSlotInactive(slot, SlotNotActiveReason.ChangedToSpec);
                    }
                }
            }
        }

        #endregion

        #region Commands

        private void Command_requestsub(string commandName, string parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot playerSlot = playerData.PlayerSlot;
            if (playerSlot == null)
                return;

            playerSlot.IsSubRequested = true;

            _chat.SendArenaMessage(player.Arena, $"{player.Name} has requested to be subbed out.");
            SendSubAvailablityNotificationToQueuedPlayers(playerSlot.MatchData);
        }

        private void Command_sub(string commandName, string parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (playerData.PlayerSlot is not null)
            {
                _chat.SendMessage(player, $"You are already playing in a match.");
                return;
            }

            // Find the next available slot, if one exists.
            PlayerSlot subSlot = null;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                if (!_queueDictionary.TryGetValue(parameters, out TeamVersusMatchmakingQueue queue))
                {
                    if (!queue.ContainsSoloPlayer(player))
                    {
                        _chat.SendMessage(player, $"You must be queued as a solo to use ?{CommandNames.Sub}");
                        return;
                    }

                    // TODO:
                    //if(subSlot.CanBeSubbed && subSlot.SubPlayer == null)
                }
            }
            else
            {
                foreach (TeamVersusMatchmakingQueue queue in _queueDictionary.Values)
                {
                    if (queue.ContainsSoloPlayer(player))
                    {
                        foreach (MatchConfiguration configuration in queue.MatchConfigurations)
                        {
                            // TODO:
                            //configuration.MatchType
                            //if(subSlot.CanBeSubbed && subSlot.SubPlayer == null)
                        }
                    }
                }
            }

            if (subSlot is null)
                return;

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
                _chat.SendMessage(player, $"You are being moved to arena {arena.Name} as a sub for an ongoing match. Please stand by.");

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
            Player player = slot.SubPlayer;
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return; // no ongoing sub for the slot

            if (!slot.CanBeSubbed)
            {
                CancelInProgressSub(slot, true);
                return;
            }

            MatchData matchData = slot.MatchData;
            Arena arena = player.Arena;
            if (arena is null || !string.Equals(arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
            {
                CancelInProgressSub(slot, true);
                return;
            }

            // TODO: If the slot is in use by a player that requested to be subbed, then wait for that player to reach full energy, lagout to spec, or leave before replacing.

            HashSet<Player> toNotify = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(matchData, toNotify);
                toNotify.Add(player);

                if (toNotify.Count > 0)
                {
                    _chat.SendSetMessage(toNotify, $"{player.Name} in for {slot.PlayerName} [Lives: {slot.Lives}]");
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(toNotify);
            }

            _matchmakingQueues.SetPlayingAsSub(player);

            if (slot.Status == PlayerSlotStatus.Playing
                && slot.Player is not null
                && slot.Player.Arena == arena
                && slot.Player.Ship != ShipType.Spec)
            {
                // First, move the old player to spectator mode.
                _game.SetShip(slot.Player, ShipType.Spec);
            }

            AssignSlot(slot, player);
            SetShipAndFreq(slot, true, null);
        }

        private void CancelInProgressSub(PlayerSlot slot, bool notify)
        {
            if (slot == null)
                return;

            Player player = slot.SubPlayer;
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            playerData.SubSlot = null;
            slot.SubPlayer = null;

            // Remove the 'playing' state which requeues the player back into the queues.
            // In this scenario, the player should get added back into their original queue positions rather than at the end of the queues.
            _matchmakingQueues.UnsetPlaying(player, true);

            if (notify)
            {
                _chat.SendMessage(player, $"The ?sub attempt was cancelled.");
            }
        }

        private void Command_return(string commandName, string parameters, Player player, ITarget target)
        {
            if (player.Ship != ShipType.Spec
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                return;
            }

            PlayerSlot slot = playerData.PlayerSlot;
            if (slot == null)
            {
                _chat.SendMessage(player, $"return: You are not in a match.");
                return;
            }

            MatchData matchData = slot.MatchData;
            if (matchData.Status != MatchStatus.Initializing && matchData.Status != MatchStatus.InProgress)
            {
                return;
            }

            if (slot.LagOuts >= matchData.Configuration.MaxLagOuts)
            {
                _chat.SendMessage(player, $"return: You cannot return to the match because you have exceeded the maximum # of LagOuts: {matchData.Configuration.MaxLagOuts}.");
                return;
            }

            if (player.Arena == null || !string.Equals(player.Arena.Name, matchData.ArenaName, StringComparison.OrdinalIgnoreCase))
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

            PlayerSlot slot = playerData.PlayerSlot;
            if (slot is null)
                return; // Another player may have subbed in.

            SetShipAndFreq(slot, true, null);

            _mainloopTimer.ClearTimer<PlayerSlot>(MainloopTimer_ProcessInactiveSlot, slot);

            if (slot.SubPlayer != null)
            {

            }
        }

        private void Command_restart(string commandName, string parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requested to restart the game. Vote using: ?restart [y|n]");
            //_chat.SendMessage(, $"{player.Name} [agreed with|vetoed] the restart request. (For:{forVotes}, Against:{againstVotes})");
            //_chat.SendMessage(, $"The vote to ?restart has expired.");
        }

        private void Command_randomize(string commandName, string parameters, Player player, ITarget target)
        {
            //_chat.SendMessage(, $"{player.Name} requests to re-randomize the teams and restart the game. To agree, type: ?randomize");
            //_chat.SendMessage(, $"{player.Name} agreed to ?randomize. ({forVotes}/5)");
            //_chat.SendMessage(, $"The vote to ?randomize has expired.");
        }

        private void Command_end(string commandName, string parameters, Player player, ITarget target)
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

        private void Command_draw(string commandName, string parameters, Player player, ITarget target)
        {
        }

        private void Command_sc(string commandName, string parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            PlayerSlot playerSlot = playerData.PlayerSlot;
            if (playerSlot == null)
            {
                _chat.SendMessage(player, $"sc: You're not playing in a match.");
                return;
            }

            if (playerSlot.AllowShipChangeCutoff != null && playerSlot.AllowShipChangeCutoff < DateTime.UtcNow)
            {
                if (!int.TryParse(parameters, out int shipNumber) || shipNumber < 1 || shipNumber > 8)
                {
                    _chat.SendMessage(player, "sc: Invalid ship type specified.");
                    return;
                }

                ShipType ship = (ShipType)(shipNumber - 1);
                _game.SetShip(player, ship);

                _chat.SendArenaMessage(player.Arena, $"{player.Name} changed to a {ship}.");
            }
            else if (playerSlot.Lives > 0)
            {
                if (!int.TryParse(parameters, out int shipNumber) || shipNumber < 1 || shipNumber > 8)
                {
                    _chat.SendMessage(player, "sc: Invalid ship type specified.");
                    return;
                }

                ShipType ship = (ShipType)(shipNumber - 1);
                playerData.NextShip = ship;

                _chat.SendMessage(player, $"sc: Your next ship will be a {ship}.");
            }
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

                if (!queue.GetMatch(matchConfiguration, matchData.TeamPlayerSlots))
                    continue;

                //
                // Start the game
                //

                // Reserve the match.
                matchData.Status = MatchStatus.Initializing;

                // Try to find the arena
                Arena arena = _arenaManager.FindArena(matchData.ArenaName); // This will only find the arena if it already exists and is running.

                foreach (PlayerSlot playerSlot in matchData.TeamPlayerSlots)
                {
                    Player player = playerSlot.Player;

                    AssignSlot(playerSlot, player);

                    playerSlot.Status = PlayerSlotStatus.Waiting;

                    if (arena == null || player.Arena != arena)
                    {
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
                            matchData = new MatchData(matchIdentifier, matchConfiguration);
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
            if (slot == null || player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (playerData.PlayerSlot != null)
            {
                // The player is already assigned a slot, unassign it first.
                UnassignSlot(playerData.PlayerSlot);
            }

            UnassignSlot(slot);

            _playerSlotDictionary.Add(player.Name, slot);
            slot.PlayerName = player.Name;
            slot.Player = player;
            playerData.PlayerSlot = slot;

            slot.LagOuts = 0;
        }

        private void UnassignSlot(PlayerSlot slot)
        {
            if (slot == null)
                return;

            if (string.IsNullOrWhiteSpace(slot.PlayerName))
                return; // The slot is not assigned to a player.

            _playerSlotDictionary.Remove(slot.PlayerName);
            slot.PlayerName = null;

            if (slot.Player != null)
            {
                if (slot.Player.TryGetExtraData(_pdKey, out PlayerData playerData))
                {
                    playerData.PlayerSlot = null;
                    playerData.IsReturning = false;
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

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(matchData, players);

                if (slot.CanBeSubbed)
                {
                    // The slot is immediately available to sub.
                    _availableSubSlots.AddLast(slot);

                    if (reason == SlotNotActiveReason.ChangedToSpec)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode. The slot is now available for ?sub.");
                    }
                    else if (reason == SlotNotActiveReason.LeftArena)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena. The slot is now available for ?sub.");
                    }

                    SendSubAvailablityNotificationToQueuedPlayers(matchData);
                }
                else
                {
                    // The slot will become available to sub if the player doesn't return in time.
                    if (reason == SlotNotActiveReason.ChangedToSpec)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has changed to spectator mode and has {matchData.Configuration.AllowSubAfter.TotalSeconds} seconds to return. [Lagouts: {slot.LagOuts}]");
                    }
                    else if (reason == SlotNotActiveReason.LeftArena)
                    {
                        _chat.SendSetMessage(players, $"{slot.PlayerName} has left the arena and has {matchData.Configuration.AllowSubAfter.TotalSeconds} seconds to return. [Lagouts: {slot.LagOuts}]");
                    }

                    _mainloopTimer.SetTimer(MainloopTimer_ProcessInactiveSlot, (int)matchData.Configuration.AllowSubAfter.TotalSeconds, Timeout.Infinite, slot, slot);
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
                    _chat.SendSetMessage(players, $"A slot in an ongoing {matchData.Configuration.MatchType} match is available to be subbed. To sub use: ?sub {matchData.Configuration.MatchType}");
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        private bool MainloopTimer_ProcessInactiveSlot(PlayerSlot slot)
        {
            if (slot == null)
                return false;

            // The player assigned to the slot has not returned in time. Therefore, it is now available for ?sub.
            // The player can still ?return before another player takes the slot.

            _availableSubSlots.AddLast(slot);

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(slot.MatchData, players);

                _chat.SendSetMessage(players, $"{slot.PlayerName}'s slot is now available for ?sub.");
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
            if (matchData == null || players == null)
                return;

            // Players in the match.
            foreach (PlayerSlot slot in matchData.TeamPlayerSlots)
            {
                if (slot.Player != null)
                {
                    players.Add(slot.Player);
                }
            }

            // Players in the arena and on the spec freq.
            Arena arena = _arenaManager.FindArena(matchData.ArenaName);
            if (arena != null)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Freq == arena.SpecFreq)
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
            if (matchData == null)
                return;

            _mainloop.QueueMainWorkItem(DoMatchInitialization, matchData);

            void DoMatchInitialization(MatchData matchData)
            {
                if (matchData.Status == MatchStatus.Initializing)
                {
                    Arena arena = _arenaManager.FindArena(matchData.ArenaName);
                    if (arena == null)
                        return;

                    // Check that all players are in the arena.
                    foreach (PlayerSlot playerSlot in matchData.TeamPlayerSlots)
                    {
                        if (playerSlot.Status != PlayerSlotStatus.Waiting
                            || playerSlot.Player == null
                            || playerSlot.Player.Arena != arena
                            || !playerSlot.Player.TryGetExtraData(_pdKey, out PlayerData playerData)
                            || !playerData.HasEnteredArena)
                        {
                            // Can't start
                            return;
                        }
                    }

                    // Start the match.
                    int numTeams = matchData.TeamPlayerSlots.GetLength(0);
                    int playersPerTeam = matchData.TeamPlayerSlots.GetLength(1);
                    for (int teamIdx = 0; teamIdx < numTeams; teamIdx++)
                    {
                        MapCoordinate[] startLocations = matchData.Configuration.Boxes[matchData.MatchIdentifier.BoxIdx].TeamStartLocations[teamIdx];
                        int startLocationIdx = startLocations.Length == 1 ? 0 : _prng.Number(0, startLocations.Length - 1);
                        MapCoordinate startLocation = startLocations[startLocationIdx];

                        for (int slotIdx = 0; slotIdx < playersPerTeam; slotIdx++)
                        {
                            PlayerSlot playerSlot = matchData.TeamPlayerSlots[teamIdx, slotIdx];
                            Player player = playerSlot.Player;
                            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                                continue;

                            SetShipAndFreq(playerSlot, false, startLocation);
                        }
                    }

                    matchData.Status = MatchStatus.InProgress;

                    // TODO: Fire a callback to notify the stats module that the match has started.
                }
            }
        }

        void SetShipAndFreq(PlayerSlot slot, bool isRefill, MapCoordinate? startLocation)
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
                if (playerData.NextShip != null)
                    ship = playerData.NextShip.Value;
                else
                    playerData.NextShip = ship = ShipType.Warbird;
            }

            // TODO: better logic for freq #'s for when there are multiple boxes, this just assumes there will never be 100+ teams
            short freq = (short)((slot.MatchData.MatchIdentifier.BoxIdx * 100) + slot.Identifier.TeamIdx);

            slot.Status = PlayerSlotStatus.Playing;
            slot.InactiveTimestamp = null;

            // Spawn the player (this is automatically in the center).
            _game.SetShipAndFreq(player, ship, freq);

            // Warp the player to the starting location.
            if (startLocation != null)
            {
                _game.WarpTo(player, startLocation.Value.X, startLocation.Value.Y);
            }

            if (isRefill)
            {
                // Adjust the player's items to the prior remaining amounts.
                SetRemainingItems(slot);
            }

            void SetRemainingItems(PlayerSlot slot)
            {
                Player player = slot.Player;
                if (player is null)
                    return;

                Arena arena = player.Arena;
                if (arena == null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData arenaData))
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

        private void QueueCheckForMatchCompletion(MatchData matchData)
        {
            if (matchData == null)
                return;

            _mainloopTimer.SetTimer(CheckForMatchCompletion, (int)matchData.Configuration.WinConditionDelay.TotalMilliseconds, Timeout.Infinite, matchData, matchData);
        }

        private bool CheckForMatchCompletion(MatchData matchData)
        {
            if (matchData == null)
                return false;

            int numTeams = matchData.TeamPlayerSlots.GetLength(0);
            int playersPerTeam = matchData.TeamPlayerSlots.GetLength(1);
            int remainingTeams = 0;
            int lastRemainingTeamIdx = 0;

            for (int teamIdx = 0; teamIdx < numTeams; teamIdx++)
            {
            }

            return false;
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

        private class MatchData
        {
            public MatchData(MatchIdentifier matchIdentifier, MatchConfiguration configuration)
            {
                MatchIdentifier = matchIdentifier;
                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ArenaName = Arena.CreateArenaName(Configuration.ArenaBaseName, MatchIdentifier.ArenaNumber);
                Status = MatchStatus.None;
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
            public MatchStatus Status;
            public readonly PlayerSlot[,] TeamPlayerSlots;
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
            /// The time the slot became inactive (player changed to spec or left the arena).
            /// This will allow us to figure out when the slot can be given to a substibute player.
            /// </summary>
            public DateTime? InactiveTimestamp;

            /// <summary>
            /// The # of times the player has left play.
            /// </summary>
            public int LagOuts;

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
                        || (InactiveTimestamp != null
                            && (DateTime.UtcNow - InactiveTimestamp.Value) >= MatchData.Configuration.AllowSubAfter
                        )
                    )
                );

            /// <summary>
            /// The player that is in the process of being subbed into the slot.
            /// </summary>
            public Player SubPlayer;

            /// <summary>
            /// The cutoff timestamp that the player can ship change (after death or at the start of a match).
            /// </summary>
            public DateTime? AllowShipChangeCutoff;

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
                MatchData = matchData ?? throw new ArgumentNullException(nameof(matchData));
                Identifier = identifier;
            }

            public void ResetSubFields()
            {
                SubPlayer = null;
                LagOuts = 0;
                IsSubRequested = false;
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
            public PlayerSlot PlayerSlot = null;

            /// <summary>
            /// The slot the player is in the process of being subbed into.
            /// </summary>
            public PlayerSlot SubSlot;

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
        }

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
                if (obj == null)
                    return false;

                obj.IFreqManagerEnforcerAdvisorToken = null;

                return true;
            }
        }

        /// <summary>
        /// A type that wraps either a <see cref="Core.Player"/> or <see cref="IPlayerGroup"/>.
        /// </summary>
        public readonly record struct QueuedPlayerOrGroup
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
                    while (node != null && node.ValueRef.Timestamp <= item.ValueRef.Timestamp)
                    {
                        node = node.Next;
                    }

                    if (node != null)
                    {
                        _queue.AddBefore(node, item);
                    }
                    else
                    {
                        _queue.AddLast(item);
                    }
                }
            }

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
                    if (pog.Player != null)
                        soloPlayers?.Add(pog.Player);
                    else if(pog.Group != null)
                        groups?.Add(pog.Group);
                }
            }

            public bool Remove(Player player)
            {
                LinkedListNode<QueuedPlayerOrGroup> node = _queue.First;
                while (node != null)
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
                while (node != null)
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

                List<LinkedListNode<QueuedPlayerOrGroup>> pending = new(); // TODO: object pooling
                List<LinkedListNode<QueuedPlayerOrGroup>> pendingSolo = new(); // TODO: object pooling

                try
                {
                    for (int teamIdx = 0; teamIdx < numTeams; teamIdx++)
                    {
                        LinkedListNode<QueuedPlayerOrGroup> node = _queue.First;
                        if (node == null)
                            break; // Cannot fill the team.

                        ref QueuedPlayerOrGroup pog = ref node.ValueRef;

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
                                foreach (LinkedListNode<QueuedPlayerOrGroup> soloNode in pendingSolo)
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
                        for (int i = pending.Count - 1; i >= 0; i--)
                        {
                            LinkedListNode<QueuedPlayerOrGroup> node = pending[i];
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
        }

        private static class CommandNames
        {
            public const string RequestSub = "requestsub";
            public const string Sub = "sub";
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