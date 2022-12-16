using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Game;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for banners (the small 12 x 8 bitmap image a player can choose to display next to their name).
    /// </summary>
    [CoreModuleInfo]
    public class Banners : IModule, IBanners
    {
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ILogManager _logManager;
        private INetwork _network;
        private IPlayerData _playerData;

        private PlayerDataKey<PlayerData> _pdKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ILogManager logManager,
            INetwork network,
            IPlayerData playerData)
        {
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _network.AddPacket(C2SPacketType.Banner, Packet_Banner);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _network.RemovePacket(C2SPacketType.Banner, Packet_Banner);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                // A biller module could have set the player's banner prior to this.
                CheckAndSendBanner(player, false, false);

                // Send everyone else's banner to the player.
                _playerData.Lock();

                try
                {
                    foreach (Player other in _playerData.Players)
                    {
                        if (other.Status == PlayerState.Playing
                            && other.Arena == arena
                            && other != player
                            && other.TryGetExtraData(_pdKey, out PlayerData opd))
                        {
                            lock (opd.Lock)
                            {
                                if (opd.Status == BannerStatus.Good)
                                {
                                    S2C_Banner packet = new((short)other.Id, in opd.Banner);
                                    _network.SendToOne(player, ref packet, NetSendFlags.Reliable | NetSendFlags.PriorityP1);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private void Packet_Banner(Player player, byte[] data, int length)
        {
            if (length != C2S_Banner.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Banners), player, $"Bad C2S banner packet (length={length}).");
                return;
            }

            // This implicitly catches setting from pre-playing states.
            if (player.Arena == null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Banners), player, $"Tried to set a banner from outside an arena.");
                return;
            }

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, "You must be in spectator mode to set a banner.");
                _logManager.LogP(LogLevel.Info, nameof(Banners), player, "Tried to set a banner while in a ship.");
                return;
            }

            ref C2S_Banner pkt = ref MemoryMarshal.AsRef<C2S_Banner>(data);
            SetBanner(player, in pkt.Banner, true);
            _logManager.LogP(LogLevel.Drivel, nameof(Banners), player, "Set banner.");
        }

        public void SetBanner(Player player, in Banner banner, bool isFromPlayer)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            lock (pd.Lock)
            {
                pd.Banner = banner; // copy
                pd.Status = BannerStatus.Pending;

                if (player.Status == PlayerState.Playing)
                    CheckAndSendBanner(player, true, isFromPlayer);
            }
        }

        private void CheckAndSendBanner(Player player, bool notify, bool isFromPlayer)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            lock (pd.Lock)
            {
                if (pd.Status == BannerStatus.NoBanner)
                    return;

                if (pd.Status == BannerStatus.Pending)
                {
                    if (IsAllowedBanner(player))
                    {
                        pd.Status = BannerStatus.Good;
                    }
                    else
                    {
                        pd.Status = BannerStatus.NoBanner;

                        if (isFromPlayer)
                        {
                            _logManager.LogP(LogLevel.Drivel, nameof(Banners), player, "Denied permission to use a banner.");
                        }
                    }
                }

                if (pd.Status == BannerStatus.Good)
                {
                    Arena arena = player.Arena;
                    if (arena == null) // This can be null if the banner is from a biller module, and we'll come back later in the PlayerActionCallback to send it.
                        return;

                    // send to everyone
                    S2C_Banner packet = new((short)player.Id, pd.Banner);
                    _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable | NetSendFlags.PriorityN1);

                    if (notify)
                    {
                        SetBannerCallback.Fire(arena, player, in pd.Banner, isFromPlayer);
                    }
                }
            }
        }

        private bool IsAllowedBanner(Player player) => player != null && _capabilityManager.HasCapability(player, Constants.Capabilities.SetBanner);

        #region Helper types

        private enum BannerStatus
        {
            NoBanner = 0,
            Pending,
            Good,
        }

        private class PlayerData : IPooledExtraData
        {
            public Banner Banner;
            public BannerStatus Status;

            public readonly object Lock = new();

            public void Reset()
            {
                Banner = default;
                Status = BannerStatus.NoBanner;
            }
        }

        #endregion
    }
}
