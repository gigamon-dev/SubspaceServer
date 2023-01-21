using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to watch damage done to players.
    /// </summary>
    [CoreModuleInfo]
    public class WatchDamage : IModule, IWatchDamage
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken<IWatchDamage> _iWatchDamageRegistrationToken;

        private PlayerDataKey<PlayerData> _pdKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _network.AddPacket(C2SPacketType.Damage, Packet_Damage);
            _commandManager.AddCommand("watchdamage", Command_watchdamage);

            _iWatchDamageRegistrationToken = broker.RegisterInterface<IWatchDamage>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iWatchDamageRegistrationToken);

            _commandManager.RemoveCommand("watchdamage", Command_watchdamage);
            _network.RemovePacket(C2SPacketType.Damage, Packet_Damage);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IWatchDamage

        bool IWatchDamage.TryAddWatch(Player player, Player target)
        {
            if (player == null || target == null)
                return false;

            if (!target.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                return false;

            bool added = targetPlayerData.PlayersWatching.Add(player);

            if (added && targetPlayerData.WatchCount == 1)
            {
                ToggleWatch(target, true);
            }

            return added;
        }

        bool IWatchDamage.TryRemoveWatch(Player player, Player target)
        {
            if (player == null || target == null)
                return false;

            if (!target.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                return false;

            bool removed = targetPlayerData.PlayersWatching.Remove(player);

            if (removed && targetPlayerData.WatchCount == 0)
            {
                ToggleWatch(target, false);
            }

            return removed;
        }

        void IWatchDamage.ClearWatch(Player player, bool includeWatchesOnPlayer)
        {
            if (player == null)
                return;

            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    ((IWatchDamage)this).TryRemoveWatch(player, otherPlayer);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (includeWatchesOnPlayer)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    return;

                HashSet<Player> toRemove = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    toRemove.UnionWith(playerData.PlayersWatching);

                    foreach (Player otherPlayer in toRemove)
                    {
                        ((IWatchDamage)this).TryRemoveWatch(otherPlayer, player);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(toRemove);
                }
            }
        }

        void IWatchDamage.AddCallbackWatch(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (playerData.WatchCount == 0)
                ToggleWatch(player, true);

            playerData.CallbackWatchCount++;
        }

        void IWatchDamage.RemoveCallbackWatch(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            playerData.CallbackWatchCount--;

            if (playerData.WatchCount == 0)
                ToggleWatch(player, false);
        }

        bool IWatchDamage.TryGetWatchCount(Player player, out int playersWatching, out int callbackWatchCount)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                playersWatching = default;
                callbackWatchCount = default;
                return false;
            }

            playersWatching = playerData.PlayersWatching.Count;
            callbackWatchCount = playerData.CallbackWatchCount;
            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.LeaveArena)
            {
                ((IWatchDamage)this).ClearWatch(player, true);
            }
        }

        private void Packet_Damage(Player player, byte[] data, int length, NetReceiveFlags flags)
        {
            if (player.Status != PlayerState.Playing)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (length < C2S_WatchDamageHeader.Length + DamageData.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(WatchDamage), player, $"Invalid C2S WatchDamage packet length ({length}).");
                return;
            }

            ref C2S_WatchDamageHeader c2sHeader = ref MemoryMarshal.AsRef<C2S_WatchDamageHeader>(data);
            Span<DamageData> c2sDamageSpan = MemoryMarshal.Cast<byte, DamageData>(
                data.AsSpan(C2S_WatchDamageHeader.Length, length - C2S_WatchDamageHeader.Length));

            if (pd.PlayersWatching.Count > 0)
            {
                Span<byte> s2cPacket = stackalloc byte[S2C_WatchDamageHeader.Length + c2sDamageSpan.Length * DamageData.Length];
                ref S2C_WatchDamageHeader s2cHeader = ref MemoryMarshal.AsRef<S2C_WatchDamageHeader>(s2cPacket);
                Span<DamageData> s2cDamageSpan = MemoryMarshal.Cast<byte, DamageData>(s2cPacket[S2C_WatchDamageHeader.Length..]);
                s2cHeader = new((short)player.Id, c2sHeader.Timestamp);
                c2sDamageSpan.CopyTo(s2cDamageSpan);

                _network.SendToSet(pd.PlayersWatching, s2cPacket, NetSendFlags.Reliable | NetSendFlags.PriorityN1);
            }

            if (pd.CallbackWatchCount > 0)
            {
                PlayerDamageCallback.Fire(arena, player, c2sHeader.Timestamp, c2sDamageSpan);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[0 | 1]",
            Description = "Turns damage watching on and off. If sent to a player, an argument of 1\n" +
            "turns it on, 0 turns it off, and no argument toggles. If sent as a\n" +
            "public command, only {?watchdamage 0} is meaningful, and it turns off\n" +
            "damage watching on all players.")]
        private void Command_watchdamage(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.Type == TargetType.Arena)
            {
                if (parameters.Equals("0", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove all subscriptions the player has on other players.
                    ((IWatchDamage)this).ClearWatch(player, false);
                    _chat.SendMessage(player, "All damage watching turned off.");
                }
            }
            else if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                if (targetPlayer.Type != ClientType.Continuum)
                {
                    _chat.SendMessage(player, $"Watchdamage requires {targetPlayer.Name} to use Continuum.");
                    return;
                }

                if (parameters.Equals("0", StringComparison.OrdinalIgnoreCase))
                {
                    // off
                    if (((IWatchDamage)this).TryRemoveWatch(player, targetPlayer))
                    {
                        _chat.SendMessage(player, $"Damage watching of {targetPlayer.Name} turned off.");
                    }
                }
                else if (parameters.Equals("1", StringComparison.OrdinalIgnoreCase))
                {
                    // on
                    if (((IWatchDamage)this).TryAddWatch(player, targetPlayer))
                    {
                        _chat.SendMessage(player, $"Damage watching of {targetPlayer.Name} turned on.");
                    }
                }
                else
                {
                    // toggle
                    if (((IWatchDamage)this).TryAddWatch(player, targetPlayer))
                    {
                        _chat.SendMessage(player, $"Damage watching of {targetPlayer.Name} turned on.");
                    }
                    else if(((IWatchDamage)this).TryRemoveWatch(player, targetPlayer))
                    {
                        _chat.SendMessage(player, $"Damage watching of {targetPlayer.Name} turned off.");
                    }
                }
            }
        }

        private void ToggleWatch(Player player, bool on)
        {
            if (player.Type == ClientType.Continuum)
            {
                Span<byte> packet = stackalloc byte[2] { (byte)S2CPacketType.ToggleDamage, on ? (byte)1 : (byte)0 };
                _network.SendToOne(player, packet, NetSendFlags.Reliable | NetSendFlags.PriorityN1);
            }
        }

        private class PlayerData : IPooledExtraData
        {
            public HashSet<Player> PlayersWatching = new();
            public int CallbackWatchCount;

            public int WatchCount => PlayersWatching.Count + CallbackWatchCount;

            void IPooledExtraData.Reset()
            {
                PlayersWatching.Clear();
                CallbackWatchCount = 0;
            }
        }
    }
}
