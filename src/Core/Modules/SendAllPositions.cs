using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using SendAllPositionsSettings = SS.Core.ConfigHelp.Constants.Arena.SendAllPositions;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that sends every position packet of the players in ships to certain players, regardless of distance,
    /// for an arena with a single game (e.g., an arena for a 4v4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Who is sent every position packet depends on the SendAllPositions:Mode setting.
    /// Other players in the arena are sent position packets using the normal rules.
    /// Players can be selected to be sent every position packet with the ?sendallpos command (Selected and InShip modes).
    /// </para>
    /// <para>
    /// The recipients are kept per arena, and only refreshed after something they depend on changes,
    /// rather than for every position packet.
    /// </para>
    /// <para>
    /// For arenas with multiple matches, the SS.Matchmaking.Modules.MatchFocus module provides the same, per match.
    /// Only one module that provides position packet recipients can be attached to an arena,
    /// so this module can't be attached to an arena that MatchFocus is attached to.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class SendAllPositions(
        IArenaManager arenaManager,
        IChat chat,
        ICommandManager commandManager,
        IConfigManager configManager,
        ILogManager logManager,
        IPlayerData playerData) : IModule, IArenaAttachableModule, IPlayerPositionAdvisor
    {
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        /// <summary>
        /// Optional, for checking whether spectators are idle.
        /// </summary>
        private IIdle? _idle;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// How often (in ticks) the recipients are refreshed, at most, for spectators going idle or becoming active again (Mode = All).
        /// Nothing is fired when that happens, so it has to be checked periodically.
        /// </summary>
        private const int IdleRefreshTicks = 100;

        private const string CommandName = "sendallpos";

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _idle = broker.GetInterface<IIdle>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _playerData.FreePlayerData(ref _pdKey);
            _arenaManager.FreeArenaData(ref _adKey);

            if (_idle is not null)
                broker.ReleaseInterface(ref _idle);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            // Only one module that provides position packet recipients is meant to be attached to an arena (MatchFocus takes precedence).
            foreach (IPlayerPositionAdvisor advisor in arena.GetAdvisors<IPlayerPositionAdvisor>())
            {
                if (advisor.ProvidesPositionRecipients)
                {
                    string otherName = advisor.GetType().Name;
                    _logManager.LogA(LogLevel.Warn, nameof(SendAllPositions), arena, $"Not attaching, {otherName} is attached to the arena and provides the position packet recipients instead.");
                    _chat.SendModMessage($"{nameof(SendAllPositions)} was not attached to arena {arena.Name}, {otherName} is attached to it and provides the position packet recipients instead.");
                    return false;
                }
            }

            ad.IPlayerPositionAdvisorToken = arena.RegisterAdvisor<IPlayerPositionAdvisor>(this);
            ad.SendAllPositionsRecipients.IsStale = true;

            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            SpectateChangedCallback.Register(arena, Callback_SpectateChanged);

            _commandManager.AddCommand(CommandName, Command_sendallpos, arena);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            _commandManager.RemoveCommand(CommandName, Command_sendallpos, arena);

            if (ad.IPlayerPositionAdvisorToken is not null)
                arena.UnregisterAdvisor(ref ad.IPlayerPositionAdvisorToken);

            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            SpectateChangedCallback.Unregister(arena, Callback_SpectateChanged);

            ad.SendAllPositionsRecipients.Clear();

            // Selections only apply while the module is attached.
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena && player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        pd.IsSendAllPositionsSelected = false;
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

        bool IPlayerPositionAdvisor.ProvidesPositionRecipients => true;

        bool IPlayerPositionAdvisor.TryGetPositionRecipients(Player player, [NotNullWhen(true)] out HashSet<Player>? sendAll, [NotNullWhen(true)] out HashSet<Player>? sendNormal)
        {
            sendAll = null;
            sendNormal = null;

            // Only the position packets of players in ships are of interest (spectators don't have their position packets sent anyway).
            if (player.Ship == ShipType.Spec)
                return false;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.SendAllPositionsMode == SendAllPositionsMode.Off)
                return false;

            if (ad.SendAllPositionsRecipients.IsStale
                || (ad.SendAllPositionsMode == SendAllPositionsMode.All && _idle is not null && ServerTick.Now - ad.SendAllPositionsRecipients.Refreshed >= IdleRefreshTicks))
            {
                RefreshRecipients(arena, ad);
            }

            if (!ad.SendAllPositionsRecipients.IsActive)
                return false;

            sendAll = ad.SendAllPositionsRecipients.SendAll;
            sendNormal = ad.SendAllPositionsRecipients.SendNormal;
            return true;
        }

        #endregion

        /// <summary>
        /// Refreshes the position packet recipients of an arena.
        /// </summary>
        private void RefreshRecipients(Arena arena, ArenaData ad)
        {
            ad.SendAllPositionsRecipients.Clear();
            ad.SendAllPositionsRecipients.IsStale = false;
            ad.SendAllPositionsRecipients.Refreshed = ServerTick.Now;

            _playerData.Lock();

            try
            {
                if (ad.SendAllPositionsMode != SendAllPositionsMode.Selected)
                {
                    // Only for a small game.
                    int playersInShips = 0;

                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Arena == arena
                            && otherPlayer.Status == PlayerState.Playing
                            && otherPlayer.Ship != ShipType.Spec)
                        {
                            playersInShips++;
                        }
                    }

                    if (playersInShips > ad.SendAllPositionsMaxPlaying)
                        return;
                }

                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Arena != arena || !otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPd))
                        continue;

                    bool isSendAll = ad.SendAllPositionsMode switch
                    {
                        SendAllPositionsMode.Selected => otherPd.IsSendAllPositionsSelected,
                        SendAllPositionsMode.InShip => otherPlayer.Ship != ShipType.Spec || otherPd.IsSendAllPositionsSelected,
                        SendAllPositionsMode.All => otherPlayer.Ship != ShipType.Spec || IsActiveSpectator(otherPlayer, otherPd, arena, ad),
                        _ => false,
                    };

                    if (isSendAll)
                        ad.SendAllPositionsRecipients.SendAll.Add(otherPlayer);
                    else
                        ad.SendAllPositionsRecipients.SendNormal.Add(otherPlayer);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (ad.SendAllPositionsRecipients.SendAll.Count > 0)
                ad.SendAllPositionsRecipients.IsActive = true;
            else
                ad.SendAllPositionsRecipients.Clear(); // Nobody to send every packet to. Use the normal rules.
        }

        /// <summary>
        /// Gets whether a spectator is actively watching the game: following a player in a ship, and not idle.
        /// </summary>
        private bool IsActiveSpectator(Player spectator, PlayerData pd, Arena arena, ArenaData ad)
        {
            Player? target = pd.SpectateTarget;
            if (target is null
                || target.Arena != arena
                || target.Status != PlayerState.Playing
                || target.Ship == ShipType.Spec)
            {
                return false;
            }

            // Idle spectators are sent position packets using the normal rules, until they're no longer idle.
            IIdle? idle = _idle;
            if (idle is not null && idle.GetIdle(spectator) >= ad.SendAllPositionsSpectatorIdleLimit)
                return false;

            return true;
        }

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[on | off]",
            Description = """
                Selects a player to be sent every position packet of the players in ships, regardless of distance.
                Only for when SendAllPositions:Mode is Selected (for anyone) or InShip (for spectators).
                Sent to a player: on selects them, off unselects them, and no argument toggles.
                Sent publicly: on (or no argument) selects the players that are currently in ships, and off unselects everyone.
                A selection lasts until the player leaves the arena.
                """)]
        private void Command_sendallpos(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.SendAllPositionsMode != SendAllPositionsMode.Selected && ad.SendAllPositionsMode != SendAllPositionsMode.InShip)
            {
                _chat.SendMessage(player, $"?{CommandName} only applies when SendAllPositions:Mode is {nameof(SendAllPositionsMode.Selected)} or {nameof(SendAllPositionsMode.InShip)}. It is {ad.SendAllPositionsMode}.");
                return;
            }

            if (!TryParseOnOff(parameters, out bool? on))
            {
                _chat.SendMessage(player, $"Usage: ?{CommandName} [on | off]");
                return;
            }

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (targetPlayer.Arena != arena || !targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPd))
                    return;

                bool select = on ?? !targetPd.IsSendAllPositionsSelected;
                if (targetPd.IsSendAllPositionsSelected != select)
                {
                    targetPd.IsSendAllPositionsSelected = select;
                    ad.SendAllPositionsRecipients.IsStale = true;
                }

                if (select)
                    _chat.SendMessage(player, $"{targetPlayer.Name} is selected to be sent every position packet (Mode: {ad.SendAllPositionsMode}).");
                else
                    _chat.SendMessage(player, $"{targetPlayer.Name} is not selected to be sent every position packet (Mode: {ad.SendAllPositionsMode}).");
            }
            else if (target.Type == TargetType.Arena)
            {
                bool select = on ?? true;
                int count = 0;

                _playerData.Lock();

                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Arena != arena
                            || otherPlayer.Status != PlayerState.Playing
                            || !otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPd)
                            || otherPd.IsSendAllPositionsSelected == select)
                        {
                            continue;
                        }

                        // Selecting is for the players in ships. Unselecting is for everyone.
                        if (select && otherPlayer.Ship == ShipType.Spec)
                            continue;

                        otherPd.IsSendAllPositionsSelected = select;
                        count++;
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (count > 0)
                    ad.SendAllPositionsRecipients.IsStale = true;

                if (select)
                    _chat.SendMessage(player, $"Selected {count} player(s) in ships to be sent every position packet (Mode: {ad.SendAllPositionsMode}).");
                else
                    _chat.SendMessage(player, $"Unselected {count} player(s) from being sent every position packet (Mode: {ad.SendAllPositionsMode}).");
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

        #region Callbacks

        [ConfigHelp<SendAllPositionsMode>("SendAllPositions", "Mode", ConfigScope.Arena, Default = SendAllPositionsMode.Off,
            Description = """
                Who is sent every position packet of the players in ships, regardless of distance. Off: nobody.
                Selected: the players selected with the ?sendallpos command, in a ship or spectating (regardless of SendAllPositions:MaxPlaying).
                InShip: players in ships, and spectators selected with the ?sendallpos command.
                All: players in ships, and spectators that are following a player in a ship and are not idle.
                InShip and All only apply while the arena has at most SendAllPositions:MaxPlaying players in ships.
                Everyone else is sent position packets using the normal rules.
                This requires the SS.Core.Modules.SendAllPositions module to be attached to the arena.
                """)]
        [ConfigHelp<int>("SendAllPositions", "MaxPlaying", ConfigScope.Arena, Default = 16, Min = 0,
            Description = "The maximum # of players in ships for every position packet to be sent, regardless of distance. Not used when SendAllPositions:Mode is Selected. See SendAllPositions:Mode.")]
        [ConfigHelp<int>("SendAllPositions", "SpectatorIdleSeconds", ConfigScope.Arena, Default = 120, Min = 0,
            Description = "How long a spectator can be idle (see the SS.Core.Modules.Idle module) and still be sent every position packet. See SendAllPositions:Mode.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ConfigHandle ch = arena.Cfg!;
                ad.SendAllPositionsMode = _configManager.GetEnum(ch, "SendAllPositions", "Mode", SendAllPositionsMode.Off);
                ad.SendAllPositionsMaxPlaying = Math.Max(0, _configManager.GetInt(ch, "SendAllPositions", "MaxPlaying", SendAllPositionsSettings.MaxPlaying.Default));
                ad.SendAllPositionsSpectatorIdleLimit = TimeSpan.FromSeconds(Math.Max(0, _configManager.GetInt(ch, "SendAllPositions", "SpectatorIdleSeconds", SendAllPositionsSettings.SpectatorIdleSeconds.Default)));
                ad.SendAllPositionsRecipients.IsStale = true;
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is not null && arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                ad.SendAllPositionsRecipients.IsStale = true;
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena || action == PlayerAction.LeaveArena)
            {
                if (arena is not null && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                {
                    ad.SendAllPositionsRecipients.IsStale = true;
                }

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.SpectateTarget = null;
                    pd.IsSendAllPositionsSelected = false;
                }
            }
        }

        private void Callback_SpectateChanged(Player player, Player? target)
        {
            if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                pd.SpectateTarget = target;
            }

            Arena? arena = player.Arena;
            if (arena is not null && arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                ad.SendAllPositionsRecipients.IsStale = true;
            }
        }

        #endregion

        #region Helper types

        private class ArenaData : IResettable
        {
            // settings
            public SendAllPositionsMode SendAllPositionsMode;
            public int SendAllPositionsMaxPlaying;
            public TimeSpan SendAllPositionsSpectatorIdleLimit;

            public AdvisorRegistrationToken<IPlayerPositionAdvisor>? IPlayerPositionAdvisorToken;

            /// <summary>
            /// The position packet recipients, for sending every position packet regardless of distance.
            /// </summary>
            public readonly PositionRecipients SendAllPositionsRecipients = new();

            public bool TryReset()
            {
                SendAllPositionsMode = SendAllPositionsMode.Off;
                SendAllPositionsMaxPlaying = 0;
                SendAllPositionsSpectatorIdleLimit = TimeSpan.Zero;
                IPlayerPositionAdvisorToken = null;
                SendAllPositionsRecipients.Reset();
                return true;
            }
        }

        /// <summary>
        /// The players to send position packets to, for sending every position packet regardless of distance.
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
                Refreshed = default;
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The player that the player is spectating (following), if any.
            /// </summary>
            public Player? SpectateTarget;

            /// <summary>
            /// Whether the player was selected (with the ?sendallpos command) to be sent every position packet, regardless of distance.
            /// </summary>
            public bool IsSendAllPositionsSelected;

            public bool TryReset()
            {
                SpectateTarget = null;
                IsSendAllPositionsSelected = false;
                return true;
            }
        }

        #endregion
    }
}
