using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that keeps track of which players are idle and which players are marked available/not available.
    /// </summary>
    /// <remarks>
    /// A player is considered idle if they haven't been active for <see cref="s_idleLimit"/> or longer.
    /// A player is considered to be active when:
    /// <list type="bullet">
    ///     <item>in a ship and is actively moving (rotation, thrust, fire a weapon) or drops a brick</item>
    ///     <item>requests to change arenas</item>
    ///     <item>sends a chat message</item>
    ///     <item>requests to spectate a player</item>>
    ///     <item>requests to change ship or freq</item>>
    /// </list>
    /// </remarks>
    public sealed class Idle(
        ICapabilityManager capabilityManager,
        IChat chat,
        ICommandManager commandManager,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, IIdle
    {
        // Required dependencies
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        // Optional dependencies
        private INetwork? _network;
        private IChatNetwork? _chatNetwork;

        // Component registrations
        private InterfaceRegistrationToken<IIdle>? _iIdleToken;
        private PlayerDataKey<IdlePlayerData> _pdKey;

        private static readonly TimeSpan s_idleLimit = TimeSpan.FromSeconds(120);

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = _playerData.AllocatePlayerData<IdlePlayerData>();

            _network = broker.GetInterface<INetwork>();
            _chatNetwork = broker.GetInterface<IChatNetwork>();

            if (_network is not null)
            {
                _network.AddPacket(C2SPacketType.GotoArena, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.Chat, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.SpecRequest, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.SetFreq, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.SetShip, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.Brick, Packet_NotIdle);
                _network.AddPacket(C2SPacketType.Position, Packet_Position);
            }

            if (_chatNetwork is not null)
            {
                _chatNetwork.AddHandler("GO", ChatHandler_NotIdle);
                _chatNetwork.AddHandler("CHANGEFREQ", ChatHandler_NotIdle);
                _chatNetwork.AddHandler("SEND", ChatHandler_NotIdle);
            }

            _commandManager.AddCommand("notavailable", Command_NotAvailable);
            _commandManager.AddCommand("nav", Command_NotAvailable);
            _commandManager.AddCommand("available", Command_Available);
            _commandManager.AddCommand("av", Command_Available);
            _commandManager.AddCommand("idles", Command_Idles);
            _commandManager.AddCommand("actives", Command_Actives);

            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _iIdleToken = broker.RegisterInterface<IIdle>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iIdleToken) != 0)
                return false;

            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _commandManager.RemoveCommand("notavailable", Command_NotAvailable);
            _commandManager.RemoveCommand("nav", Command_NotAvailable);
            _commandManager.RemoveCommand("available", Command_Available);
            _commandManager.RemoveCommand("av", Command_Available);
            _commandManager.RemoveCommand("idles", Command_Idles);
            _commandManager.RemoveCommand("actives", Command_Actives);

            _network?.RemovePacket(C2SPacketType.GotoArena, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.Chat, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.SpecRequest, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.SetFreq, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.SetShip, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.Brick, Packet_NotIdle);
            _network?.RemovePacket(C2SPacketType.Position, Packet_Position);

            _chatNetwork?.RemoveHandler("GO", ChatHandler_NotIdle);
            _chatNetwork?.RemoveHandler("CHANGEFREQ", ChatHandler_NotIdle);
            _chatNetwork?.RemoveHandler("SEND", ChatHandler_NotIdle);

            if (_network is not null)
            {
                broker.ReleaseInterface(ref _network);
            }

            if (_chatNetwork is not null)
            {
                broker.ReleaseInterface(ref _chatNetwork);
            }

            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IIdle

        TimeSpan IIdle.GetIdle(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out IdlePlayerData? playerData))
                return TimeSpan.Zero;

            DateTime now = DateTime.UtcNow;
            return now - (playerData.LastActive ?? now);
        }

        void IIdle.ResetIdle(Player player)
        {
            ResetIdle(player);
        }

        bool IIdle.IsAvailable(Player player)
        {
            return player is not null
                && player.Status == PlayerState.Playing
                && player.Type != ClientType.Fake
                && player.TryGetExtraData(_pdKey, out IdlePlayerData? playerData)
                && !playerData.NotAvailable;
        }

        #endregion

        #region Packet handlers, chat handlers, callbacks

        private void Packet_NotIdle(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            ResetIdle(player);
        }

        private void Packet_Position(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (player.Ship == ShipType.Spec)
                return;

            if (data.Length != C2S_PositionPacket.Length && data.Length != C2S_PositionPacket.LengthWithExtra)
                return;

            ref readonly C2S_PositionPacket pos = ref MemoryMarshal.AsRef<C2S_PositionPacket>(data);
            if ((pos.Status & PlayerPositionStatus.Inert) == 0 || pos.Weapon.Type != WeaponCodes.Null)
            {
                ResetIdle(player);
            }
        }

        private void ChatHandler_NotIdle(Player player, ReadOnlySpan<char> message)
        {
            ResetIdle(player);
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.Connect)
            {
                if (!player.TryGetExtraData(_pdKey, out IdlePlayerData? playerData))
                    return;

                playerData.LastActive = DateTime.UtcNow;
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.Arena | CommandTarget.Player,
            Args = "",
            Description = """
                Marks you as Not Available. Certain games will prevent you from being picked when Not Available.
                Staff members can mark other players by sending the command as a private message.
                """)]
        private void Command_NotAvailable(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
                    return;
            }
            else
            {
                targetPlayer = player;
            }

            if (targetPlayer.Type == ClientType.Fake)
            {
                _chat.SendMessage(player, "Fake players cannot be set.");
                return;
            }

            if (!targetPlayer.TryGetExtraData(_pdKey, out IdlePlayerData? playerData))
                return;

            playerData.NotAvailable = true;
            _chat.SendMessage(player, $"Set {((targetPlayer == player) ? "you" : targetPlayer.Name)} as not available.");
        }

        [CommandHelp(
            Targets = CommandTarget.Arena | CommandTarget.Player,
            Args = "",
            Description = """
                Marks you as Available. Certain games require you to be Available to be picked.
                Staff members can mark other players by sending the command as a private message.
                """)]
        private void Command_Available(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
                    return;
            }
            else
            {
                targetPlayer = player;
            }

            if (targetPlayer.Type == ClientType.Fake)
            {
                _chat.SendMessage(player, "Fake players cannot be set.");
                return;
            }

            if (!targetPlayer.TryGetExtraData(_pdKey, out IdlePlayerData? playerData))
                return;

            playerData.NotAvailable = false;
            _chat.SendMessage(player, $"Set {((targetPlayer == player) ? "you" : targetPlayer.Name)} as available.");
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = "[-g|-c] [-s|-p]",
            Description = """
                Lists everyone in the arena who is Idle.
                Additional filters:
                -g : game clients only (Continuum or VIE)
                -c : chat clients only (ChatNet)
                -s : spectators only
                -p : playing only (in a ship)
                See also: ?actives
                """)]
        private void Command_Idles(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            bool gameClientsOnly = parameters.Contains("-g", StringComparison.OrdinalIgnoreCase);
            bool chatClientsOnly = parameters.Contains("-c", StringComparison.OrdinalIgnoreCase);

            if (gameClientsOnly && chatClientsOnly)
            {
                _chat.SendMessage(player, "Idles: Both -g and -c cannot be selected at the same time.");
                return;
            }

            bool specOnly = parameters.Contains("-s", StringComparison.OrdinalIgnoreCase);
            bool playingOnly = parameters.Contains("-p", StringComparison.OrdinalIgnoreCase);

            if (specOnly && playingOnly)
            {
                _chat.SendMessage(player, "Idles: Both -s and -p cannot be selected at the same time.");
                return;
            }

            DateTime now = DateTime.UtcNow;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                int total = 0;

                _playerData.Lock();

                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Arena == arena
                            && otherPlayer.Type != ClientType.Fake
                            && (!gameClientsOnly || otherPlayer.IsStandard)
                            && (!chatClientsOnly || otherPlayer.IsChat)
                            && (!specOnly || otherPlayer.Ship == ShipType.Spec)
                            && (!playingOnly || otherPlayer.Ship != ShipType.Spec)
                            && otherPlayer.TryGetExtraData(_pdKey, out IdlePlayerData? playerData)
                            && (now - (playerData.LastActive ?? now)) >= s_idleLimit)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(otherPlayer.Name);
                            total++;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                _chat.SendMessage(player, $"Arena '{arena.Name}': {total} idle");
                _chat.SendWrappedText(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = "[-g|-c] [-s|-p]",
            Description = """
                Lists everyone in the arena who is Available and not Idle.
                Additional filters:
                -g : game clients only (Continuum or VIE)
                -c : chat clients only (ChatNet)
                -s : spectators only
                -p : playing only (in a ship)
                See also: ?idles
                """)]
        private void Command_Actives(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            bool gameClientsOnly = parameters.Contains("-g", StringComparison.OrdinalIgnoreCase);
            bool chatClientsOnly = parameters.Contains("-c", StringComparison.OrdinalIgnoreCase);

            if (gameClientsOnly && chatClientsOnly)
            {
                _chat.SendMessage(player, "Actives: Both -g and -c cannot be selected at the same time.");
                return;
            }

            bool specOnly = parameters.Contains("-s", StringComparison.OrdinalIgnoreCase);
            bool playingOnly = parameters.Contains("-p", StringComparison.OrdinalIgnoreCase);

            if (specOnly && playingOnly)
            {
                _chat.SendMessage(player, "Actives: Both -s and -p cannot be selected at the same time.");
                return;
            }

            DateTime now = DateTime.UtcNow;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                int total = 0;

                _playerData.Lock();

                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Arena == arena
                            && otherPlayer.Type != ClientType.Fake
                            && (!gameClientsOnly || otherPlayer.IsStandard)
                            && (!chatClientsOnly || otherPlayer.IsChat)
                            && (!specOnly || otherPlayer.Ship == ShipType.Spec)
                            && (!playingOnly || otherPlayer.Ship != ShipType.Spec)
                            && otherPlayer.TryGetExtraData(_pdKey, out IdlePlayerData? playerData)
                            && !playerData.NotAvailable
                            && (now - (playerData.LastActive ?? now)) < s_idleLimit)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(otherPlayer.Name);
                            total++;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                _chat.SendMessage(player, $"Arena '{arena.Name}': {total} active");
                _chat.SendWrappedText(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private void ResetIdle(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out IdlePlayerData? playerData))
                return;

            playerData.LastActive = DateTime.UtcNow;
        }

        #region Helper types

        private class IdlePlayerData : IResettable
        {
            public DateTime? LastActive;
            public bool NotAvailable;

            bool IResettable.TryReset()
            {
                LastActive = null;
                NotAvailable = false;

                return true;
            }
        }

        #endregion
    }
}
