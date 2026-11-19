using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using System.Diagnostics.CodeAnalysis;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks the association of players with ongoing matches, 
    /// whether it be because a player: participated in the match, is playing in the match, or is spectating the match.
    /// </summary>
    /// <remarks>
    /// This module also provides logic for position packet filtering as an <see cref="IPlayerPositionAdvisor"/>.
    /// </remarks>
    public sealed class MatchFocus(
        IComponentBroker broker,
        IChat chat,
        ICommandManager commandManager,
        IConfigManager configManager,
        IGame game,
        ILogManager logManager,
        IMainloop mainloop,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, IArenaAttachableModule, IMatchFocus, IPlayerPositionAdvisor, IBricksAdvisor, IKillAdvisor
    {
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly IGame _game = game ?? throw new ArgumentNullException(nameof(game));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        /// <summary>
        /// Optional, for checking whether spectators are idle.
        /// </summary>
        private IIdle? _idle;

        private InterfaceRegistrationToken<IMatchFocus>? _iMatchFocusRegistrationToken;

        private PlayerDataKey<PlayerData> _playerDataKey;
        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);
        private readonly Dictionary<IMatch, MatchFocusData> _matchDictionary = new(TargetMatchCount);

        private readonly static ObjectPool<ArenaData> s_arenaDataPool = new DefaultObjectPool<ArenaData>(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);
        private readonly static ObjectPool<MatchFocusData> s_matchFocusPool = new DefaultObjectPool<MatchFocusData>(new DefaultPooledObjectPolicy<MatchFocusData>(), TargetMatchCount);

        /// <summary>
        /// The expected upper limit on the # of simultaneous matches.
        /// </summary>
        /// <remarks>
        /// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
        /// </remarks>
        private const int TargetMatchCount = 64;

        /// <summary>
        /// How often (in ticks) a match's position packet recipients are refreshed, at most, for spectators going idle or becoming active again (SendAllPositions = All).
        /// Nothing is fired when that happens, so it has to be checked periodically.
        /// </summary>
        private const int SendAllPositionsIdleRefreshTicks = 100;

        private const string SendAllPosCommandName = "sendallpos";

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _idle = broker.GetInterface<IIdle>();

            _playerDataKey = _playerData.AllocatePlayerData<PlayerData>();

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchStartingCallback.Register(broker, Callback_MatchStarting);
            MatchEndedCallback.Register(broker, Callback_MatchEnded);
            MatchAddPlayingCallback.Register(broker, Callback_MatchAddPlaying);
            MatchRemovePlayingCallback.Register(broker, Callback_MatchRemovePlaying);

            _iMatchFocusRegistrationToken = broker.RegisterInterface<IMatchFocus>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMatchFocusRegistrationToken) != 0)
                return false;

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchStartingCallback.Unregister(broker, Callback_MatchStarting);
            MatchEndedCallback.Unregister(broker, Callback_MatchEnded);
            MatchAddPlayingCallback.Unregister(broker, Callback_MatchAddPlaying);
            MatchRemovePlayingCallback.Unregister(broker, Callback_MatchRemovePlaying);

            _playerData.FreePlayerData(ref _playerDataKey);

            if (_idle is not null)
                broker.ReleaseInterface(ref _idle);

            return true;
        }

        [ConfigHelp<bool>("SS.Matchmaking.MatchFocus", "FilterKillPackets", ConfigScope.Arena, Default = false,
            Description = "Whether to suppress kill notification packets for players not focused on the same match. Spectators watching a specific match see only that match's kills; unattached spectators see all kills.")]
        [ConfigHelp<SendAllPositionsMode>("SS.Matchmaking.MatchFocus", "SendAllPositions", ConfigScope.Arena, Default = SendAllPositionsMode.Off,
            Description = """
                Who is sent every position packet of the players in ships in a match, regardless of distance. Off: nobody.
                Selected: the players focused on the match that were selected with the ?sendallpos command, in a ship or spectating
                (regardless of SS.Matchmaking.MatchFocus:SendAllPositionsMaxPlaying).
                InShip: players in ships in the match, and spectators of the match selected with the ?sendallpos command.
                All: players in ships in the match, and spectators of the match that are following a player in a ship in it and are not idle.
                InShip and All only apply while the match has at most SS.Matchmaking.MatchFocus:SendAllPositionsMaxPlaying players in ships.
                Everyone else focused on the match is sent position packets using the normal rules.
                """)]
        [ConfigHelp<int>("SS.Matchmaking.MatchFocus", "SendAllPositionsMaxPlaying", ConfigScope.Arena, Default = 16, Min = 0,
            Description = "The maximum # of players in ships in a match for every position packet to be sent, regardless of distance. Not used when SS.Matchmaking.MatchFocus:SendAllPositions is Selected. See SS.Matchmaking.MatchFocus:SendAllPositions.")]
        [ConfigHelp<int>("SS.Matchmaking.MatchFocus", "SendAllPositionsSpectatorIdleSeconds", ConfigScope.Arena, Default = 120, Min = 0,
            Description = "How long a spectator can be idle (see the SS.Core.Modules.Idle module) and still be sent every position packet. See SS.Matchmaking.MatchFocus:SendAllPositions.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
            {
                arenaData = s_arenaDataPool.Get();
                _arenaDataDictionary.Add(arena, arenaData);
            }

            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            SpectateChangedCallback.Register(arena, Callback_SpectateChanged);
            arenaData.IPlayerPositionAdvisorRegistrationToken = arena.RegisterAdvisor<IPlayerPositionAdvisor>(this);
            arenaData.IBricksAdvisorRegistrationToken = arena.RegisterAdvisor<IBricksAdvisor>(this);
            arenaData.IKillAdvisorRegistrationToken = arena.RegisterAdvisor<IKillAdvisor>(this);
            arenaData.FilterKillPackets = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.MatchFocus", "FilterKillPackets", false);
            arenaData.SendAllPositionsMode = _configManager.GetEnum(arena.Cfg!, "SS.Matchmaking.MatchFocus", "SendAllPositions", SendAllPositionsMode.Off);
            arenaData.SendAllPositionsMaxPlaying = Math.Max(0, _configManager.GetInt(arena.Cfg!, "SS.Matchmaking.MatchFocus", "SendAllPositionsMaxPlaying", 16));
            arenaData.SendAllPositionsSpectatorIdleLimit = TimeSpan.FromSeconds(Math.Max(0, _configManager.GetInt(arena.Cfg!, "SS.Matchmaking.MatchFocus", "SendAllPositionsSpectatorIdleSeconds", 120)));

            // The recipients depend on the settings.
            foreach (MatchFocusData matchFocusData in _matchDictionary.Values)
            {
                matchFocusData.SendAllPositionsRecipients.IsStale = true;
            }

            _commandManager.AddCommand(SendAllPosCommandName, Command_sendallpos, arena);

            // This module provides the position packet recipients per match.
            // The SendAllPositions module does it for the arena as a whole, which would conflict, so it can't be attached to the same arena.
            foreach (IPlayerPositionAdvisor advisor in arena.GetAdvisors<IPlayerPositionAdvisor>())
            {
                if (advisor is SendAllPositions)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(MatchFocus), arena, $"Detaching {nameof(SendAllPositions)}, this module provides the position packet recipients per match instead.");
                    _chat.SendModMessage($"{nameof(SendAllPositions)} is being detached from arena {arena.Name}, {nameof(MatchFocus)} is attached to it and provides the position packet recipients per match instead.");

                    // Detaching can't be done while a module is being attached, so do it afterwards.
                    _mainloop.QueueMainWorkItem(MainloopWork_DetachSendAllPositions, arena);
                    break;
                }
            }

            return true;
        }

        private async void MainloopWork_DetachSendAllPositions(Arena arena)
        {
            IModuleManager? moduleManager = _broker.GetInterface<IModuleManager>();
            if (moduleManager is null)
                return;

            try
            {
                if (await moduleManager.DetachModuleAsync(typeof(SendAllPositions), arena))
                {
                    _logManager.LogA(LogLevel.Info, nameof(MatchFocus), arena, $"Detached {nameof(SendAllPositions)}.");
                }
                else
                {
                    _logManager.LogA(LogLevel.Warn, nameof(MatchFocus), arena, $"Failed to detach {nameof(SendAllPositions)}.");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogA(LogLevel.Error, nameof(MatchFocus), arena, $"Error detaching {nameof(SendAllPositions)}. {ex.Message}");
            }
            finally
            {
                _broker.ReleaseInterface(ref moduleManager);
            }
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            _commandManager.RemoveCommand(SendAllPosCommandName, Command_sendallpos, arena);

            if (!arena.UnregisterAdvisor(ref arenaData.IPlayerPositionAdvisorRegistrationToken))
                return false;

            if (!arena.UnregisterAdvisor(ref arenaData.IBricksAdvisorRegistrationToken))
                return false;

            if (!arena.UnregisterAdvisor(ref arenaData.IKillAdvisorRegistrationToken))
                return false;

            if (!_arenaDataDictionary.Remove(arena))
                return false;

            s_arenaDataPool.Return(arenaData);

            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            SpectateChangedCallback.Unregister(arena, Callback_SpectateChanged);

            // Selections (to be sent every position packet) only apply while the module is attached.
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena)
                        ClearSendAllPositionsSelected(player);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return true;
        }

        #endregion

        #region IPlayerPositionAdvisor

        bool IPlayerPositionAdvisor.EditIndividualPositionPacket(Player player, Player toPlayer, ref C2S_PositionPacket positionPacket, ref ExtraPositionData extra, ref int extraLength)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return false;

            if (!toPlayer.TryGetExtraData(_playerDataKey, out PlayerData? toPlayerData))
                return false;

            if (playerData.PlayingInMatch == GetFocusedMatchData(toPlayer))
            {
                // The player whose position packet is under consideration is in the match that the toPlayer is focused on (spectating or playing in).
                // OR
                // (null == null) The player whose position packet is under consideration is not in a match and the toPlayer is not focused on a match.
                return false;
            }

            // Drop the packet.
            positionPacket.X = positionPacket.Y = -1;
            return true;
        }

        bool IPlayerPositionAdvisor.ProvidesPositionRecipients => true;

        bool IPlayerPositionAdvisor.TryGetPositionRecipients(Player player, [NotNullWhen(true)] out HashSet<Player>? sendAll, [NotNullWhen(true)] out HashSet<Player>? sendNormal)
        {
            sendAll = null;
            sendNormal = null;

            // Only the position packets of players in ships are of interest (spectators don't have their position packets sent anyway).
            if (player.Ship == ShipType.Spec)
                return false;

            Arena? arena = player.Arena;
            if (arena is null
                || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData)
                || arenaData.SendAllPositionsMode == SendAllPositionsMode.Off)
            {
                return false;
            }

            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData)
                || playerData.PlayingInMatch is not MatchFocusData matchFocusData)
            {
                return false;
            }

            // The recipients are kept per match, and only refreshed after something they depend on changes.
            if (matchFocusData.SendAllPositionsRecipients.IsStale
                || matchFocusData.SendAllPositionsRecipients.Arena != arena
                || (arenaData.SendAllPositionsMode == SendAllPositionsMode.All && _idle is not null && ServerTick.Now - matchFocusData.SendAllPositionsRecipients.Refreshed >= SendAllPositionsIdleRefreshTicks))
            {
                RefreshRecipients(arena, arenaData, matchFocusData);
            }

            if (!matchFocusData.SendAllPositionsRecipients.IsActive)
                return false;

            sendAll = matchFocusData.SendAllPositionsRecipients.SendAll;
            sendNormal = matchFocusData.SendAllPositionsRecipients.SendNormal;
            return true;
        }

        /// <summary>
        /// Refreshes the position packet recipients of a match.
        /// </summary>
        private void RefreshRecipients(Arena arena, ArenaData arenaData, MatchFocusData matchFocusData)
        {
            matchFocusData.SendAllPositionsRecipients.Clear();
            matchFocusData.SendAllPositionsRecipients.IsStale = false;
            matchFocusData.SendAllPositionsRecipients.Arena = arena;
            matchFocusData.SendAllPositionsRecipients.Refreshed = ServerTick.Now;

            if (arenaData.SendAllPositionsMode != SendAllPositionsMode.Selected)
            {
                // Only for a small match.
                int playersInShips = 0;
                foreach (Player otherPlayer in matchFocusData.Playing)
                {
                    if (otherPlayer.Arena == arena && otherPlayer.Ship != ShipType.Spec)
                        playersInShips++;
                }

                if (playersInShips > arenaData.SendAllPositionsMaxPlaying)
                    return;
            }

            // The players focused on the match, which are the only ones allowed the packets (see EditIndividualPositionPacket).
            // Note: A player can be in both Playing and Spectators. That's fine since which set the player is added to is decided the same way for both.
            foreach (Player otherPlayer in matchFocusData.Playing)
            {
                AddRecipient(otherPlayer);
            }

            foreach (Player otherPlayer in matchFocusData.Spectators)
            {
                AddRecipient(otherPlayer);
            }

            if (matchFocusData.SendAllPositionsRecipients.SendAll.Count > 0)
                matchFocusData.SendAllPositionsRecipients.IsActive = true;
            else
                matchFocusData.SendAllPositionsRecipients.Clear(); // Nobody to send every packet to. Use the normal rules.

            void AddRecipient(Player otherPlayer)
            {
                if (otherPlayer.Arena != arena || !otherPlayer.TryGetExtraData(_playerDataKey, out PlayerData? otherPlayerData))
                    return;

                bool isSendAll = arenaData.SendAllPositionsMode switch
                {
                    SendAllPositionsMode.Selected => otherPlayerData.IsSendAllPositionsSelected,
                    SendAllPositionsMode.InShip => otherPlayer.Ship != ShipType.Spec || otherPlayerData.IsSendAllPositionsSelected,
                    SendAllPositionsMode.All => otherPlayer.Ship != ShipType.Spec || IsActiveSpectator(otherPlayer, otherPlayerData, matchFocusData, arenaData),
                    _ => false,
                };

                if (isSendAll)
                    matchFocusData.SendAllPositionsRecipients.SendAll.Add(otherPlayer);
                else
                    matchFocusData.SendAllPositionsRecipients.SendNormal.Add(otherPlayer);
            }
        }

        /// <summary>
        /// Gets whether a spectator is actively watching a match: following a player in a ship in the match, and not idle.
        /// </summary>
        private bool IsActiveSpectator(Player spectator, PlayerData spectatorData, MatchFocusData matchFocusData, ArenaData arenaData)
        {
            Player? target = spectatorData.SpectateTarget;
            if (target is null
                || target.Arena != spectator.Arena
                || target.Ship == ShipType.Spec
                || !target.TryGetExtraData(_playerDataKey, out PlayerData? targetData)
                || targetData.PlayingInMatch != matchFocusData)
            {
                return false;
            }

            // Idle spectators are sent position packets using the normal rules, until they're no longer idle.
            IIdle? idle = _idle;
            if (idle is not null && idle.GetIdle(spectator) >= arenaData.SendAllPositionsSpectatorIdleLimit)
                return false;

            return true;
        }

        #endregion

        #region IBricksAdvisor

        bool IBricksAdvisor.IsValidForPlayer(Player player, ref readonly BrickData brickData)
        {
            if (GetFocusedMatchData(player)?.Match is not IMatchData matchData)
                return true;

            // Check if the brick is for a team in the match.
            for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
            {
                ITeam team = matchData.Teams[teamIdx];
                if (team.Freq == brickData.Freq)
                    return true;
            }

            return false;
        }

        #endregion

        #region IKillAdvisor

        void IKillAdvisor.FilterKillNotification(Arena arena, Player killer, Player killed, HashSet<Player> recipients)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData) || !arenaData.FilterKillPackets)
                return;

            if (!killed.TryGetExtraData(_playerDataKey, out PlayerData? killedData))
                return;

            MatchFocusData? killedMatch = killedData.PlayingInMatch;

            HashSet<Player>? toRemove = null;
            try
            {
                foreach (Player recipient in recipients)
                {
                    if (GetFocusedMatchData(recipient) != killedMatch)
                        (toRemove ??= _objectPoolManager.PlayerSetPool.Get()).Add(recipient);
                }

                if (toRemove is not null)
                    foreach (Player player in toRemove)
                        recipients.Remove(player);
            }
            finally
            {
                if (toRemove is not null)
                {
                    _objectPoolManager.PlayerSetPool.Return(toRemove);
                }
            }
        }

        #endregion

        #region IMatchFocus

        bool IMatchFocus.TryGetPlayers(IMatch match, HashSet<Player> players, MatchFocusReasons? filterReasons, Arena? arenaFilter)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return false;

            // Participants
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Participant) != 0)
            {
                foreach (Player player in matchFocusData.Participants)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            // Playing
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Playing) != 0)
            {
                foreach (Player player in matchFocusData.Playing)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            // Spectating
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Spectating) != 0)
            {
                foreach (Player player in matchFocusData.Spectators)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            return true;
        }

        IMatch? IMatchFocus.GetPlayingMatch(Player player)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return null;

            return playerData.PlayingInMatch?.Match;
        }

        IMatch? IMatchFocus.GetFocusedMatch(Player player)
        {
            if (player is null)
                return null;

            return GetFocusedMatchData(player)?.Match;
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[on | off]",
            Description = """
                Selects a player to be sent every position packet of the players in ships in the match they're focused on, regardless of distance.
                Only for when SS.Matchmaking.MatchFocus:SendAllPositions is Selected (for anyone) or InShip (for spectators).
                Sent to a player: on selects them, off unselects them, and no argument toggles.
                Sent publicly: applies to the match you're playing in or spectating.
                On (or no argument) selects the players that are currently in ships in it, and off unselects everyone focused on it.
                A selection lasts until the player leaves the arena or the player's match ends.
                """)]
        private void Command_sendallpos(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            SendAllPositionsMode mode = arenaData.SendAllPositionsMode;
            if (mode != SendAllPositionsMode.Selected && mode != SendAllPositionsMode.InShip)
            {
                _chat.SendMessage(player, $"?{SendAllPosCommandName} only applies when SS.Matchmaking.MatchFocus:SendAllPositions is {nameof(SendAllPositionsMode.Selected)} or {nameof(SendAllPositionsMode.InShip)}. It is {mode}.");
                return;
            }

            if (!TryParseOnOff(parameters, out bool? on))
            {
                _chat.SendMessage(player, $"Usage: ?{SendAllPosCommandName} [on | off]");
                return;
            }

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (targetPlayer.Arena != arena || !targetPlayer.TryGetExtraData(_playerDataKey, out PlayerData? targetPlayerData))
                    return;

                bool select = on ?? !targetPlayerData.IsSendAllPositionsSelected;
                if (targetPlayerData.IsSendAllPositionsSelected != select)
                {
                    targetPlayerData.IsSendAllPositionsSelected = select;
                    InvalidateRecipients(targetPlayerData);
                }

                if (select)
                {
                    if (GetFocusedMatchData(targetPlayer) is null)
                        _chat.SendMessage(player, $"{targetPlayer.Name} is selected to be sent every position packet (Mode: {mode}), but isn't playing in or spectating a match.");
                    else
                        _chat.SendMessage(player, $"{targetPlayer.Name} is selected to be sent every position packet (Mode: {mode}).");
                }
                else
                {
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not selected to be sent every position packet (Mode: {mode}).");
                }
            }
            else if (target.Type == TargetType.Arena)
            {
                MatchFocusData? matchFocusData = GetFocusedMatchData(player);
                if (matchFocusData is null)
                {
                    _chat.SendMessage(player, $"You must be playing in or spectating a match to use ?{SendAllPosCommandName} publicly.");
                    return;
                }

                bool select = on ?? true;
                int count = 0;

                // Selecting is for the players in ships. Unselecting is for everyone focused on the match.
                foreach (Player otherPlayer in matchFocusData.Playing)
                {
                    if (otherPlayer.Arena == arena && (!select || otherPlayer.Ship != ShipType.Spec))
                        SetSelected(otherPlayer);
                }

                if (!select)
                {
                    foreach (Player otherPlayer in matchFocusData.Spectators)
                    {
                        if (otherPlayer.Arena == arena)
                            SetSelected(otherPlayer);
                    }
                }

                if (select)
                    _chat.SendMessage(player, $"Selected {count} player(s) in ships in the match to be sent every position packet (Mode: {mode}).");
                else
                    _chat.SendMessage(player, $"Unselected {count} player(s) in the match from being sent every position packet (Mode: {mode}).");

                void SetSelected(Player otherPlayer)
                {
                    if (otherPlayer.TryGetExtraData(_playerDataKey, out PlayerData? otherPlayerData)
                        && otherPlayerData.IsSendAllPositionsSelected != select)
                    {
                        otherPlayerData.IsSendAllPositionsSelected = select;
                        InvalidateRecipients(otherPlayerData);
                        count++;
                    }
                }
            }
        }

        /// <summary>
        /// Parses an on/off command argument.
        /// </summary>
        /// <param name="parameters">The argument.</param>
        /// <param name="on"><see langword="true"/> for on, <see langword="false"/> for off, <see langword="null"/> for no argument.</param>
        /// <returns><see langword="true"/> if the argument was valid. Otherwise, <see langword="false"/>.</returns>
        private static bool TryParseOnOff(ReadOnlySpan<char> parameters, out bool? on)
        {
            parameters = parameters.Trim();

            if (parameters.IsEmpty)
            {
                on = null;
                return true;
            }

            if (parameters.Equals("on", StringComparison.OrdinalIgnoreCase) || parameters.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                on = true;
                return true;
            }

            if (parameters.Equals("off", StringComparison.OrdinalIgnoreCase) || parameters.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                on = false;
                return true;
            }

            on = null;
            return false;
        }

        #endregion

        #region Callback handlers

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;

            string? playerName = player.Name;
            if (playerName is null)
                return;

            if (action == PlayerAction.Connect)
            {
                foreach (MatchFocusData matchFocusData in _matchDictionary.Values)
                {
                    if (matchFocusData.DisconnectedParticipantNameSet.Remove(playerName))
                    {
                        // A participant that previously disconnected has reconnected.
                        matchFocusData.Participants.Add(player);
                    }

                    if (matchFocusData.DisconnectedPlayingNameSet.Remove(playerName))
                    {
                        // A playing player that previously disconnected has reconnected.
                        SetPlaying(player, playerData, matchFocusData);
                    }
                }
            }
            else if (action == PlayerAction.EnterArena)
            {
                // Which of a match's players are in the same arena changed.
                InvalidateRecipients(playerData);
            }
            else if (action == PlayerAction.LeaveArena)
            {
                playerData.SpectateTarget = null;
                playerData.IsSendAllPositionsSelected = false;

                // Which of a match's players are in the same arena changed.
                InvalidateRecipients(playerData);

                if (playerData.SpectatingMatch is not null)
                {
                    // The SpectateChanged callback will get invoked. However, the handler will not clear the spectating state.
                    // To the handler, it will look like the player is just no longer directly following the player.
                    // Therefore, we need to clear the spectating state here since the player is no longer in the same arena.
                    ClearSpectating(player, playerData);
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                foreach (MatchFocusData matchFocusData in _matchDictionary.Values)
                {
                    if (matchFocusData.Participants.Remove(player))
                    {
                        matchFocusData.DisconnectedParticipantNameSet.Add(playerName);
                    }
                }

                if (playerData.PlayingInMatch is not null)
                {
                    ClearPlaying(player, playerData, true);
                }

                // Spectators are removed when they leave the arena.
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;

            InvalidateRecipients(playerData);

            if (oldShip == ShipType.Spec && newShip != ShipType.Spec)
            {
                // The player was in spectator mode and changed into a ship.

                // If the player was spectating a match, clear it.
                ClearSpectating(player, playerData);

                MatchFocusData? matchFocusData = playerData.PlayingInMatch;
                if (matchFocusData is null)
                    return;

                // The player is in a match.

                // If a player is playing and then changes to spectator mode, 
                // those that were spectating will still be spectating that player if there are no other players to spectate.
                // In this special case, they do not send a SpecRequest packet to clear.
                // If the player enters a ship, they will resume spectating that player without sending another SpecRequest packet.

                // Make sure that any players spectating the player are set as spectating the match as well.
                // There's a chance the player's match could have changed between moving to spec and entering a ship.
                SetSpectators(player, matchFocusData);
            }
        }

        private void Callback_MatchStarting(IMatch match)
        {
            if (match is null)
                return;

            MatchFocusData matchFocusData = GetOrAddMatchFocusData(match);

            HashSet<string> playingNames = _objectPoolManager.NameHashSetPool.Get();
            HashSet<Player> playing = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> spectating = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                // Playing (including those that have disconnected)
                var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (playingNames.Count > 0)
                        playingNames.Clear();

                    // Ask the advisor to provide the names of the players that are playing in the match.
                    if (advisor.TryGetPlaying(match, playingNames))
                    {
                        foreach (string name in playingNames)
                        {
                            Player? player = _playerData.FindPlayer(name);
                            if (player is not null)
                                playing.Add(player);
                            else
                                matchFocusData.DisconnectedPlayingNameSet.Add(name);
                        }

                        // Players have been provided by an advisor. No need to query the rest.
                        break;
                    }
                }

                foreach (Player player in playing)
                {
                    if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                        continue;

                    SetPlaying(player, playerData, matchFocusData);
                }

                // Spectators
                _game.GetSpectators(playing, spectating);
                foreach (Player player in spectating)
                {
                    if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                        continue;

                    SetSpectating(player, playerData, matchFocusData);
                }
            }
            finally
            {
                _objectPoolManager.NameHashSetPool.Return(playingNames);
                _objectPoolManager.PlayerSetPool.Return(playing);
                _objectPoolManager.PlayerSetPool.Return(spectating);
            }
        }

        private void Callback_MatchEnded(IMatch match)
        {
            if (match is null)
                return;

            if (!_matchDictionary.Remove(match, out MatchFocusData? matchFocusData))
                return;

            // Selections (to be sent every position packet) last until the player's match ends.
            foreach (Player player in matchFocusData.Playing)
            {
                ClearSendAllPositionsSelected(player);
            }

            foreach (Player player in matchFocusData.Spectators)
            {
                ClearSendAllPositionsSelected(player);
            }

            // Disassociate anyone spectating the match.
            while (matchFocusData.Spectators.Count > 0)
            {
                foreach (Player player in matchFocusData.Spectators)
                {
                    ClearSpectating(player);

                    // Enumerator can't be used after clear.
                    break;
                }
            }

            // Disassociate anyone that was playing in the match.
            while (matchFocusData.Playing.Count > 0)
            {
                foreach (Player player in matchFocusData.Playing)
                {
                    ClearPlaying(player);

                    // Enumerator can't be used after clear.
                    break;
                }
            }

            // Return the object to the pool.
            // Note: This also resets the object, clearing the remaining data.
            s_matchFocusPool.Return(matchFocusData);
        }

        private void Callback_MatchAddPlaying(IMatch match, string playerName, Player? player)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return;

            if (player is not null)
            {
                if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                    return;

                SetPlaying(player, playerData, matchFocusData);

                // Make sure that any players spectating the player are set as spectating the match as well.
                SetSpectators(player, matchFocusData);
            }
            else if (playerName is not null)
            {
                matchFocusData.DisconnectedPlayingNameSet.Add(playerName);
            }
        }

        private void Callback_MatchRemovePlaying(IMatch match, string playerName, Player? player)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return;

            if (player is not null)
            {
                if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                    return;

                if (playerData.PlayingInMatch == matchFocusData)
                {
                    ClearPlaying(player, playerData);
                }
            }
            else if (playerName is not null)
            {
                matchFocusData.DisconnectedPlayingNameSet.Remove(playerName);
            }
        }

        private void Callback_SpectateChanged(Player player, Player? target)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;

            _logManager.LogP(LogLevel.Drivel, nameof(MatchFocus), player, $"Spectate changed to: {(target is null ? "(none)" : target.Name)}");

            playerData.SpectateTarget = target;

            IMatch? match = null;

            if (target is not null)
            {
                // Check if the target player is playing in a match.
                var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
                foreach (var advisor in advisors)
                {
                    match = advisor.GetMatch(target);
                    if (match is not null)
                        break;
                }
            }

            if (match is not null)
            {
                SetSpectating(player, playerData, GetOrAddMatchFocusData(match));
            }
            else
            {
                // No match.
                // However, only clear spectating if it was a switch to a new target player.
                // When just moving off of a player, let the player continue spectating the match they're watching.
                if (target is not null)
                {
                    ClearSpectating(player, playerData);
                }
            }

            // Whether the player is following a player in the match changed.
            InvalidateRecipients(playerData);
        }

        #endregion

        private MatchFocusData GetOrAddMatchFocusData(IMatch match)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
            {
                matchFocusData = s_matchFocusPool.Get();
                matchFocusData.Match = match;
                _matchDictionary.Add(match, matchFocusData);
            }

            return matchFocusData;
        }

        private void SetPlaying(Player player, PlayerData playerData, MatchFocusData matchFocusData)
        {
            IMatch? oldMatch = GetFocusedMatchData(player)?.Match;

            if (playerData.SpectatingMatch is not null)
            {
                ClearSpectating(player, playerData);
            }

            if (playerData.PlayingInMatch is not null)
            {
                if (playerData.PlayingInMatch == matchFocusData)
                {
                    // Already set as playing in this match.
                    return;
                }

                // The player was playing in another match.
                ClearPlaying(player, playerData, false, false);
            }

            matchFocusData.Playing.Add(player);
            matchFocusData.Participants.Add(player);
            playerData.PlayingInMatch = matchFocusData;
            matchFocusData.SendAllPositionsRecipients.IsStale = true;

            MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
        }

        private void ClearPlaying(Player player, PlayerData? playerData = null, bool isDisconnect = false, bool invokeCallback = true)
        {
            if (playerData is null && !player.TryGetExtraData(_playerDataKey, out playerData))
                return;

            MatchFocusData? matchFocusData = playerData.PlayingInMatch;
            if (matchFocusData is not null)
            {
                IMatch? oldMatch = invokeCallback ? GetFocusedMatchData(player)?.Match : null;

                matchFocusData.Playing.Remove(player);
                playerData.PlayingInMatch = null;
                matchFocusData.SendAllPositionsRecipients.IsStale = true;

                if (isDisconnect)
                {
                    matchFocusData.DisconnectedPlayingNameSet.Add(player.Name!);
                }

                if (invokeCallback)
                {
                    MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
                }
            }
        }

        private void SetSpectators(Player target, MatchFocusData matchFocusData)
        {
            HashSet<Player> spectators = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                // Check if anyone is spectating the target player.
                _game.GetSpectators(target, spectators);

                // Set them as spectating the match.
                foreach (Player spectator in spectators)
                {
                    if (!spectator.TryGetExtraData(_playerDataKey, out PlayerData? spectatorData))
                        continue;

                    SetSpectating(spectator, spectatorData, matchFocusData);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(spectators);
            }
        }

        private void SetSpectating(Player player, PlayerData playerData, MatchFocusData matchFocusData)
        {
            IMatch? oldMatch = GetFocusedMatchData(player)?.Match;

            if (playerData.SpectatingMatch is not null)
            {
                if (playerData.SpectatingMatch == matchFocusData)
                {
                    // Already set as spectating this match.
                    return;
                }

                // The player was spectating a different match.
                ClearSpectating(player, playerData, false);
            }

            matchFocusData.Spectators.Add(player);
            playerData.SpectatingMatch = matchFocusData;
            matchFocusData.SendAllPositionsRecipients.IsStale = true;

            MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
        }

        private void ClearSpectating(Player player, PlayerData? playerData = null, bool invokeCallback = true)
        {
            if (playerData is null && !player.TryGetExtraData(_playerDataKey, out playerData))
                return;

            if (playerData.SpectatingMatch is not null)
            {
                IMatch? oldMatch = invokeCallback ? GetFocusedMatchData(player)?.Match : null;

                playerData.SpectatingMatch.Spectators.Remove(player);
                playerData.SpectatingMatch.SendAllPositionsRecipients.IsStale = true;
                playerData.SpectatingMatch = null;

                if (invokeCallback)
                {
                    MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
                }
            }
        }

        private MatchFocusData? GetFocusedMatchData(Player player)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return null;

            return playerData.SpectatingMatch ?? playerData.PlayingInMatch;
        }

        /// <summary>
        /// Marks the position packet recipients of the matches a player is associated with as needing to be refreshed.
        /// </summary>
        private static void InvalidateRecipients(PlayerData playerData)
        {
            if (playerData.PlayingInMatch is not null)
                playerData.PlayingInMatch.SendAllPositionsRecipients.IsStale = true;

            if (playerData.SpectatingMatch is not null)
                playerData.SpectatingMatch.SendAllPositionsRecipients.IsStale = true;
        }

        private void ClearSendAllPositionsSelected(Player player)
        {
            if (player.TryGetExtraData(_playerDataKey, out PlayerData? playerData) && playerData.IsSendAllPositionsSelected)
            {
                playerData.IsSendAllPositionsSelected = false;
                InvalidateRecipients(playerData);
            }
        }

        #region Helper types

        private class MatchFocusData : IResettable
        {
            public IMatch? Match;

            /// <summary>
            /// Players participated in the match (and are still connected).
            /// </summary>
            /// <remarks>
            /// See <see cref="DisconnectedParticipantNameSet"/> for names of participants that have disconnected.
            /// </remarks>
            public readonly HashSet<Player> Participants = new(Constants.TargetPlayerCount);

            /// <summary>
            /// Names of <see cref="Participants"/> that have disconnected from the server.
            /// If the player reconnects, this is used to add them back into the <see cref="Participants"/> collection.
            /// </summary>
            public readonly HashSet<string> DisconnectedParticipantNameSet = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Players that are currently playing in the match.
            /// </summary>
            public readonly HashSet<Player> Playing = new(Constants.TargetPlayerCount); // TODO: what do to about players that get knocked out? are they playing still? we still want to send them notifications, but what if they're allowed to join another match?

            /// <summary>
            /// Names of <see cref="Playing"/> that have disconnected from the server.
            /// If the player reconnects, this is used to add them back into the <see cref="Playing"/> collection.
            /// </summary>
            public readonly HashSet<string> DisconnectedPlayingNameSet = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Players that are spectating the match.
            /// </summary>
            public readonly HashSet<Player> Spectators = new(Constants.TargetPlayerCount);

            /// <summary>
            /// The position packet recipients for the match's players, for sending every position packet regardless of distance.
            /// </summary>
            public readonly PositionRecipients SendAllPositionsRecipients = new();

            bool IResettable.TryReset()
            {
                Match = null;
                Participants.Clear();
                DisconnectedParticipantNameSet.Clear();
                Playing.Clear();
                DisconnectedPlayingNameSet.Clear();
                Spectators.Clear();
                SendAllPositionsRecipients.Reset();
                return true;
            }
        }

        /// <summary>
        /// The players to send a match's position packets to, for sending every position packet regardless of distance.
        /// </summary>
        /// <remarks>
        /// These are kept, and only refreshed after something they depend on changes, rather than for every position packet.
        /// </remarks>
        private class PositionRecipients
        {
            /// <summary>
            /// The players to send every position packet to, regardless of distance. Only valid when <see cref="IsActive"/> is <see langword="true"/>.
            /// </summary>
            public readonly HashSet<Player> SendAll = new(Constants.TargetPlayerCount);

            /// <summary>
            /// The players to send position packets to using the normal rules. Only valid when <see cref="IsActive"/> is <see langword="true"/>.
            /// </summary>
            public readonly HashSet<Player> SendNormal = new(Constants.TargetPlayerCount);

            /// <summary>
            /// Whether the recipients apply. If not, position packets are sent using the normal rules.
            /// </summary>
            public bool IsActive;

            /// <summary>
            /// Whether the recipients need to be refreshed, because something they depend on changed.
            /// </summary>
            public bool IsStale = true;

            /// <summary>
            /// The arena that the recipients were refreshed for.
            /// </summary>
            public Arena? Arena;

            /// <summary>
            /// When the recipients were last refreshed.
            /// </summary>
            public ServerTick Refreshed;

            public void Clear()
            {
                SendAll.Clear();
                SendNormal.Clear();
                IsActive = false;
            }

            public void Reset()
            {
                Clear();
                IsStale = true;
                Arena = null;
                Refreshed = default;
            }
        }

        private class PlayerData : IResettable
        {
            public MatchFocusData? PlayingInMatch;
            public MatchFocusData? SpectatingMatch;

            /// <summary>
            /// The player that the player is spectating (following), if any.
            /// </summary>
            public Player? SpectateTarget;

            /// <summary>
            /// Whether the player was selected (with the ?sendallpos command) to be sent every position packet of the match the player is focused on.
            /// </summary>
            public bool IsSendAllPositionsSelected;

            bool IResettable.TryReset()
            {
                PlayingInMatch = null;
                SpectatingMatch = null;
                SpectateTarget = null;
                IsSendAllPositionsSelected = false;
                return true;
            }
        }

        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IPlayerPositionAdvisor>? IPlayerPositionAdvisorRegistrationToken;
            public AdvisorRegistrationToken<IBricksAdvisor>? IBricksAdvisorRegistrationToken;
            public AdvisorRegistrationToken<IKillAdvisor>? IKillAdvisorRegistrationToken;
            public bool FilterKillPackets;
            public SendAllPositionsMode SendAllPositionsMode;
            public int SendAllPositionsMaxPlaying;
            public TimeSpan SendAllPositionsSpectatorIdleLimit;

            bool IResettable.TryReset()
            {
                IPlayerPositionAdvisorRegistrationToken = null;
                IBricksAdvisorRegistrationToken = null;
                IKillAdvisorRegistrationToken = null;
                FilterKillPackets = false;
                SendAllPositionsMode = SendAllPositionsMode.Off;
                SendAllPositionsMaxPlaying = 0;
                SendAllPositionsSpectatorIdleLimit = TimeSpan.Zero;
                return true;
            }
        }

        #endregion
    }
}
