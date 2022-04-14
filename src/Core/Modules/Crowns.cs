using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    public class Crowns : IModule, ICrowns, ICrownsBehavior
    {
        private ILogManager _logManager;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<ICrowns> _iCrownsInterfaceRegistrationToken;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ILogManager logManager,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _network.AddPacket(C2SPacketType.CrownExpired, Packet_CrownExpired);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _iCrownsInterfaceRegistrationToken = broker.RegisterInterface<ICrowns>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
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

            Arena arena = player.Arena;
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

            Arena arena = player.Arena;
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

        private void Packet_CrownExpired(Player p, byte[] data, int length)
        {
            if (length != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Crowns), p, $"Invalid C2S CrownExpired packet length ({length}).");
                return;
            }

            if (p.Status != PlayerState.Playing
                || !p.Packet.HasCrown)
            {
                return;
            }

            Arena arena = p.Arena;
            if (arena == null)
                return;

            ICrownsBehavior behavior = arena.GetInterface<ICrownsBehavior>();
            if (behavior == null)
                behavior = this; // default implementation

            try
            {
                behavior.CrownExpired(p);
            }
            finally
            {
                if (behavior != this)
                    arena.ReleaseInterface(ref behavior);
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                // Make sure the player starts out without a crown.
                p.Packet.HasCrown = false;

                //
                // Send the current crown state of the players in the arena.
                //

                HashSet<Player> crownSet = _objectPoolManager.PlayerSetPool.Get();
                HashSet<Player> noCrownSet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    // Find out who has a crown and who doesn't.
                    _playerData.Lock();

                    try
                    {
                        foreach (Player player in _playerData.Players)
                        {
                            if (player.Packet.HasCrown)
                                crownSet.Add(player);
                            else
                                noCrownSet.Add(player);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    if (crownSet.Count > 1 + noCrownSet.Count)
                    {
                        // Send S2C Crown to turn on the crown for all players followed by S2C Crown to turn off the crown for those in noCrownSet.
                        // TODO: grouped packet?
                        S2C_Crown addAllPacket = new(CrownAction.On, TimeSpan.Zero, -1);
                        _network.SendToOne(p, ref addAllPacket, NetSendFlags.Reliable);

                        foreach (Player otherPlayer in noCrownSet)
                        {
                            S2C_Crown removePacket = new(CrownAction.Off, TimeSpan.Zero, (short)otherPlayer.Id);
                            _network.SendToOne(p, ref removePacket, NetSendFlags.Reliable);
                        }
                    }
                    else
                    {
                        // Send a S2C Crown packet to turn on the crown for each player in crownSet.
                        // TODO: grouped packet?
                        foreach (Player otherPlayer in crownSet)
                        {
                            S2C_Crown addPacket = new(CrownAction.On, TimeSpan.Zero, (short)otherPlayer.Id);
                            _network.SendToOne(p, ref addPacket, NetSendFlags.Reliable);
                        }
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(crownSet);
                    _objectPoolManager.PlayerSetPool.Return(noCrownSet);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                p.Packet.HasCrown = false;
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
