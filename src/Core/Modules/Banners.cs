using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
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
    public class Banners(
        IComponentBroker broker,
        ICapabilityManager capabilityManager,
        IChat chat,
        IConfigManager configManager,
        ILogManager logManager,
        INetwork network,
        IPlayerData playerData) : IModule, IBanners, IBannersAdvisor
    {
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private AdvisorRegistrationToken<IBannersAdvisor>? _iBannersAdvisor;
        private InterfaceRegistrationToken<IBanners>? _iBannersToken;

        private PlayerDataKey<PlayerData> _pdKey;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _network.AddPacket(C2SPacketType.Banner, Packet_Banner);

            _iBannersAdvisor = _broker.RegisterAdvisor<IBannersAdvisor>(this);
            _iBannersToken = _broker.RegisterInterface<IBanners>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (_broker.UnregisterInterface(ref _iBannersToken) != 0)
                return false;

            if (!_broker.UnregisterAdvisor(ref _iBannersAdvisor))
                return false;

            _network.RemovePacket(C2SPacketType.Banner, Packet_Banner);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IBanners

        bool IBanners.TryGetBanner(Player player, out Banner banner)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                banner = default;
                return false;
            }

            lock (pd.Lock)
            {
                if (!pd.Banner.IsSet)
                {
                    banner = default;
                    return false;
                }

                banner = pd.Banner;
                return true;
            }
        }

        void IBanners.SetBanner(Player player, ref readonly Banner banner)
        {
            SetBanner(player, in banner, false);
        }

        void IBanners.CheckAndSendBanner(Player player)
        {
            CheckAndSendBanner(player);
        }

        #endregion

        #region IBannerAdvisor

        [ConfigHelp<int>("Misc", "BannerPoints", ConfigScope.Arena, Default = 0,
            Description = "Number of points required to display a banner.")]
        bool IBannersAdvisor.IsAllowedBanner(Player player)
        {
            if (player is null
                || !_capabilityManager.HasCapability(player, Constants.Capabilities.SetBanner))
            {
                return false;
            }

            Arena? arena = player.Arena;
            if (arena is null)
            {
                return false;
            }

            // TODO: Add logic to automatically use a pending banner when the player passes the required point threshold.
            // This would require adding a StatChangedCallback (for being able to watch KillPoints a FlagPoints).

            int bannerPoints = _configManager.GetInt(arena.Cfg!, "Misc", "BannerPoints", ConfigHelp.Constants.Arena.Misc.BannerPoints.Default);
            if (bannerPoints > 0)
            {
                IScoreStats? scoreStats = arena.GetInterface<IScoreStats>();
                if (scoreStats is not null)
                {
                    try
                    {
                        scoreStats.GetScores(player, out int killPoints, out int flagPoints, out _, out _);

                        if ((killPoints + flagPoints) < bannerPoints)
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref scoreStats);
                    }
                }
            }

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                // A biller module could have set the player's banner prior to this.
                // Or, the player could have have just switched arenas.
                // Check if the player has a banner that can be used, and if so, send it to all the players in the arena.
                CheckAndSendBanner(player);

                // Send everyone else's banner to the player.
                _playerData.Lock();

                try
                {
                    foreach (Player other in _playerData.Players)
                    {
                        if (other.Status == PlayerState.Playing
                            && other.Arena == arena
                            && other != player
                            && other.TryGetExtraData(_pdKey, out PlayerData? opd))
                        {
                            lock (opd.Lock)
                            {
                                if (opd.Status == BannerStatus.Good)
                                {
                                    S2C_Banner packet = new((short)other.Id, ref opd.Banner);
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
            else if (action == PlayerAction.LeaveArena)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                lock (pd.Lock)
                {
                    if (pd.Status == BannerStatus.Good)
                    {
                        pd.Status = BannerStatus.Pending;
                    }
                    else if (pd.Status == BannerStatus.PendingClear)
                    {
                        pd.Status = BannerStatus.NoBanner;
                    }
                }
            }
        }

        private void Packet_Banner(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != C2S_Banner.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Banners), player, $"Bad C2S banner packet (length={data.Length}).");
                return;
            }

            // This implicitly catches setting from pre-playing states.
            if (player.Arena is null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Banners), player, "Tried to set a banner from outside an arena.");
                return;
            }

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, "You must be in spectator mode to set a banner.");
                _logManager.LogP(LogLevel.Info, nameof(Banners), player, "Tried to set a banner while in a ship.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            // Rate limit banner changes.
            lock (pd.Lock)
            {
                if (pd.LastSetByPlayer is not null
                    && (DateTime.UtcNow - pd.LastSetByPlayer.Value) < TimeSpan.FromSeconds(5))
                {
                    _chat.SendMessage(player, "You set your banner too recently.");
                    return;
                }

                pd.LastSetByPlayer = DateTime.UtcNow;
            }

            ref C2S_Banner pkt = ref MemoryMarshal.AsRef<C2S_Banner>(data);
            SetBanner(player, in pkt.Banner, true);
            _logManager.LogP(LogLevel.Drivel, nameof(Banners), player, "Set banner.");
        }

        private void SetBanner(Player player, ref readonly Banner banner, bool isFromPlayer)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if (banner.IsSet)
                {
                    // The banner is being set. We need to check if it can be used.
                    pd.Status = BannerStatus.Pending;
                }
                else
                {
                    // The banner is being cleared.
                    if (pd.Status == BannerStatus.Good)
                    {
                        // The banner was previously in use. We need to tell players that it's been cleared.
                        pd.Status = BannerStatus.PendingClear;
                    }
                    else if (pd.Status == BannerStatus.Pending)
                    {
                        // The banner was not previously in use.
                        pd.Status = BannerStatus.NoBanner;
                    }
                }

                pd.Banner = banner; // copy

                CheckAndSendBanner(player);
            }

            BannerSetCallback.Fire(player.Arena ?? _broker, player, ref pd.Banner, isFromPlayer);
        }

        private void CheckAndSendBanner(Player player)
        {
            if (player is null)
                return;

            if (player.Status != PlayerState.Playing)
                return; // The player is not playing yet, nothing to do.

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                if ((pd.Status == BannerStatus.Pending && IsAllowedBanner(player))
                    || pd.Status == BannerStatus.PendingClear)
                {
                    // Send the change to everyone.
                    S2C_Banner packet = new((short)player.Id, ref pd.Banner);
                    _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable | NetSendFlags.PriorityN1);

                    if (pd.Status == BannerStatus.Pending)
                    {
                        pd.Status = BannerStatus.Good;
                    }
                    else if (pd.Status == BannerStatus.PendingClear)
                    {
                        pd.Status = BannerStatus.NoBanner;
                    }
                }
            }


            static bool IsAllowedBanner(Player player)
            {
                Arena? arena = player.Arena;
                if (arena is null)
                    return false;

                foreach (var advisor in arena.GetAdvisors<IBannersAdvisor>())
                {
                    if (!advisor.IsAllowedBanner(player))
                        return false;
                }

                return true;
            }
        }

        #region Helper types

        private enum BannerStatus
        {
            /// <summary>
            /// No banner present.
            /// </summary>
            /// <remarks>Transitions to: <see cref="Pending"/> (banner set by biller or player).</remarks>
            NoBanner = 0,

            /// <summary>
            /// Present but waiting to be used when allowed.
            /// </summary>
            /// <remarks>Transitions to: <see cref="Good"/> or <see cref="NoBanner"/>.</remarks>
            Pending,

            /// <summary>
            /// Present and in use.
            /// </summary>
            /// <remarks>Transitions to: <see cref="PendingClear"/> (player wants to remove banner) or <see cref="Pending"/> (player changes arena).</remarks>
            Good,

            /// <summary>
            /// Previously present but removed. Needs to be cleared on other players.
            /// </summary>
            /// <remarks>Transitions to: <see cref="NoBanner"/> or <see cref="Pending"/>.</remarks>
            PendingClear,
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// Timestamp that the player last set their banner in the current session.
            /// </summary>
            public DateTime? LastSetByPlayer = null;

            /// <summary>
            /// The banner.
            /// </summary>
            public Banner Banner;

            /// <summary>
            /// Used to track the state of the banner:
            /// whether the banner is set,
            /// whether the banner is in use,
            /// whether there is a pending action on the banner.
            /// </summary>
            public BannerStatus Status;

            public readonly object Lock = new();

            public bool TryReset()
            {
                lock (Lock)
                {
                    LastSetByPlayer = null;
                    Banner = default;
                    Status = BannerStatus.NoBanner;
                }

                return true;
            }
        }

        #endregion
    }
}
