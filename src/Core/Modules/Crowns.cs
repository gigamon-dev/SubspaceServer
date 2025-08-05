﻿using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for controlling crown indicators on players.
    /// </summary>
    /// <remarks>
    /// The original use case of crowns was for implementing a King of the Hill style game.
    /// However, it is not limited to that particular use case. Crowns can be used as a 
    /// general mechanism to mark players.
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Crowns(
        ILogManager logManager,
        INetwork network,
        IPlayerData playerData) : IModule, ICrowns, ICrownsBehavior
    {
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private InterfaceRegistrationToken<ICrowns>? _iCrownsInterfaceRegistrationToken;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _network.AddPacket(C2SPacketType.CrownExpired, Packet_CrownExpired);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _iCrownsInterfaceRegistrationToken = broker.RegisterInterface<ICrowns>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iCrownsInterfaceRegistrationToken) != 0)
                return false;

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _network.RemovePacket(C2SPacketType.CrownExpired, Packet_CrownExpired);

            return true;
        }

        #endregion

        #region ICrown

        void ICrowns.ToggleOn(Arena arena, TimeSpan duration)
        {
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena)
                    {
                        player.Packet.HasCrown = true;
                        CrownToggledCallback.Fire(arena, player, true);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            S2C_Crown allOnPacket = new(CrownAction.On, duration, -1);
            _network.SendToArena(arena, null, ref allOnPacket, NetSendFlags.Reliable);
        }

        void ICrowns.ToggleOn(HashSet<Player> players, TimeSpan duration)
        {
            if (players == null)
                return;

            foreach (Player player in players)
            {
                ((ICrowns)this).ToggleOn(player, duration);
            }
        }

        void ICrowns.ToggleOn(Player player, TimeSpan duration)
        {
            if (player == null || player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena == null)
                return;

            player.Packet.HasCrown = true;
            CrownToggledCallback.Fire(arena, player, true);

            S2C_Crown onPacket = new(CrownAction.On, duration, (short)player.Id);
            _network.SendToArena(arena, null, ref onPacket, NetSendFlags.Reliable);
        }

        bool ICrowns.TryAddTime(Player player, TimeSpan additional)
        {
            return TrySendPersonalTimerChange(player, true, additional);
        }

        bool ICrowns.TrySetTime(Player player, TimeSpan duration)
        {
            return TrySendPersonalTimerChange(player, false, duration);
        }

        void ICrowns.ToggleOff(Arena arena)
        {
            if (arena == null)
                return;

            bool sendPacket = false;

            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena)
                    {
                        if (player.Packet.HasCrown)
                        {
                            player.Packet.HasCrown = false;
                            sendPacket = true;
                        }

                        CrownToggledCallback.Fire(arena, player, false);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (sendPacket)
            {
                S2C_Crown allOffPacket = new(CrownAction.Off, TimeSpan.Zero, -1);
                _network.SendToArena(arena, null, ref allOffPacket, NetSendFlags.Reliable);
            }
        }

        void ICrowns.ToggleOff(HashSet<Player> players)
        {
            if (players == null)
                return;

            foreach (Player player in players)
            {
                ((ICrowns)this).ToggleOff(player);
            }
        }

        void ICrowns.ToggleOff(Player player)
        {
            if (player == null || player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena == null)
                return;

            if (player.Packet.HasCrown)
            {
                player.Packet.HasCrown = false;

                S2C_Crown offPacket = new(CrownAction.Off, TimeSpan.Zero, (short)player.Id);
                _network.SendToArena(arena, null, ref offPacket, NetSendFlags.Reliable);
            }

            CrownToggledCallback.Fire(arena, player, false);
        }

        #endregion

        #region ICrownsBehavior

        void ICrownsBehavior.CrownExpired(Player player)
        {
            ((ICrowns)this).ToggleOff(player);
        }

        #endregion

        private void Packet_CrownExpired(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Crowns), player, $"Invalid C2S CrownExpired packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing
                || !player.Packet.HasCrown)
            {
                return;
            }

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ICrownsBehavior? behavior = arena.GetInterface<ICrownsBehavior>() ?? this;

            try
            {
                behavior.CrownExpired(player);
            }
            finally
            {
                if (behavior != this)
                    arena.ReleaseInterface(ref behavior);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.PreEnterArena)
            {
                // Make sure the player starts out without a crown.
                player.Packet.HasCrown = false;
            }
            else if (action == PlayerAction.LeaveArena)
            {
                player.Packet.HasCrown = false;
            }
        }

        private bool TrySendPersonalTimerChange(Player player, bool add, TimeSpan timeSpan)
        {
            if (player == null)
                return false;

            if (player.Status != PlayerState.Playing)
                return false;

            if (!player.Packet.HasCrown)
                return false; // can't do a crown timer change unless the player already has a crown

            S2C_CrownTimer packet = new(add, timeSpan);
            _network.SendToOne(player, ref packet, NetSendFlags.Reliable);
            return true;
        }
    }
}
