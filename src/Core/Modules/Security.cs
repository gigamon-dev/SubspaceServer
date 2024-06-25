using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that sends security check requests to clients which they must respond to.
    /// 
    /// <para>
    /// Security-wise, the requests are a countermeasure against cheating, though there is the assumption of trust in the client itself.
    /// The server requests that the clients respond in a timely fashion with checksums of:
    /// <list type="bullet">
    /// <item>The map</item>
    /// <item>Client settings</item>
    /// <item>The client executable</item>
    /// </list>
    /// Through these requests, the server may decide to kick players that fail to respond or have a checksum mismatch.
    /// </para>
    /// 
    /// <para>
    /// The requests double as a mechanism to synchronize random number generator (RNG) seeds.  
    /// The RNG seeds are used to synchronize the state of doors (open or closed) and spawn locations of prizes (greens).
    /// </para>
    /// 
    /// <para>
    /// Also, the responses from clients include data that can be used for gathering statistics on lag / packetloss.
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public class Security : IModule, ISecuritySeedSync
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabilityManager;
        private IClientSettings _clientSettings;
        private IConfigManager _configManager;
        private ILagCollect _lagCollect;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private IMapData _mapData;
        private INetwork _network;
        private IPlayerData _playerData;
        private IPrng _prng;

        private InterfaceRegistrationToken<ISecuritySeedSync> _iSecuritySeedSyncRegisrationToken;

        /// <summary>
        /// Arena data key for accessing <see cref="ArenaData"/>.
        /// </summary>
        private ArenaDataKey<ArenaData> _adKey;

        /// <summary>
        /// Player data key for accessing <see cref="PlayerData"/>.
        /// </summary>
        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// The expected length of the scrty data.
        /// </summary>
        private const int ScrtyLength = 1000;

        /// <summary>
        /// scrty contains pairs of 32-bit unsigned integers.
        /// <para>
        /// The first pair is special:
        /// scrty[0] is 0, scrty[1] is the continuum checksum
        /// </para>
        /// <para>
        /// The remaining data consists of pairs that are: seed and checksum.
        /// In other words, 
        /// scrty[2] and scrty[3] are the 1st pair with scrty[2] being the seed and scrty[3] the checksum,
        /// scrty[4] and scrty[5] are the 2nd pair, and so on...
        /// </para>
        /// </summary>
        private uint[] _scrty;

        /// <summary>
        /// The packet to send to players.
        /// </summary>
        private S2C_Security _packet = new();

        /// <summary>
        /// The continuum exe checksum from <see cref="_scrty"/>.
        /// </summary>
        private uint _continuumExeChecksum;

        /// <summary>
        /// The VIE exe checksum. See <see cref="GetVieExeChecksum(uint)"/>.
        /// </summary>
        private uint _vieExeChecksum;

        /// <summary>
        /// For synchronizing access to this class' data.
        /// </summary>
        private readonly object _lockObj = new();

        [ConfigHelp("Security", "SecurityKickoff", ConfigScope.Global, typeof(bool), DefaultValue = "false", 
            Description = "Whether to kick players off of the server for violating security checks.")]
        private bool _securityKickoff;

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IClientSettings clientSettings,
            IConfigManager configManager,
            ILagCollect lagCollect,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            INetwork network,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _adKey = arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = playerData.AllocatePlayerData<PlayerData>();

            LoadScrty();

            _securityKickoff = configManager.GetInt(configManager.Global, "Security", "SecurityKickoff", 0) != 0;

            SwitchChecksums();

            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            mainloopTimer.SetTimer(MainloopTimer_Send, 25000, 60000, new SendTimerData(),null);

            network.AddPacket(C2SPacketType.SecurityResponse, Packet_SecurityResponse);

            _iSecuritySeedSyncRegisrationToken = broker.RegisterInterface<ISecuritySeedSync>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iSecuritySeedSyncRegisrationToken) != 0)
                return false;

            _network.RemovePacket(C2SPacketType.SecurityResponse, Packet_SecurityResponse);
            _mainloopTimer.ClearTimer<SendTimerData>(MainloopTimer_Send, null);
            _mainloopTimer.ClearTimer(MainloopTimer_Check, null);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #region ISecuritySeedSync

        void ISecuritySeedSync.GetCurrentSeedInfo(out uint greenSeed, out uint doorSeed, out uint timestamp)
        {
            greenSeed = _packet.GreenSeed;
            doorSeed = _packet.DoorSeed;
            timestamp = _packet.Timestamp;
        }

        void ISecuritySeedSync.OverrideArenaSeedInfo(Arena arena, uint greenSeed, uint doorSeed, uint timestamp)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            S2C_Security overridePacket = new(greenSeed, doorSeed, timestamp, 0);
            ad.OverridePacket = overridePacket;

            // Send the packet without a key (just syncing the client, not requesting the client to respond).
            _network.SendToArena(arena, null, ref overridePacket, NetSendFlags.Reliable);

            _logManager.LogA(LogLevel.Drivel, nameof(Security), arena,
                $"Sent seeds (override): green={overridePacket.GreenSeed:X}, door={overridePacket.DoorSeed:X}, timestamp={overridePacket.Timestamp:X}.");
        }

        bool ISecuritySeedSync.RemoveArenaOverride(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.OverridePacket == null)
                return false;

            ad.OverridePacket = null;

            // Send the packet without a key (just syncing the client, not requesting the client to respond).
            S2C_Security securityPacket = _packet;
            securityPacket.Key = 0;
            _network.SendToArena(arena, null, ref securityPacket, NetSendFlags.Reliable);

            _logManager.LogA(LogLevel.Drivel, nameof(Security), arena,
                $"Sent seeds (de-override): green={securityPacket.GreenSeed:X}, door={securityPacket.DoorSeed:X}, timestamp={securityPacket.Timestamp:X}.");

            return true;
        }

        #endregion

        private void LoadScrty()
        {
            try
            {
                _scrty = new uint[ScrtyLength];

                using FileStream fs = File.OpenRead("scrty");
                using BinaryReader br = new(fs);

                for (int i = 0; i < ScrtyLength; i++)
                {
                    _scrty[i] = br.ReadUInt32(); // reads bytes as little-endian
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Info, nameof(Security), $"Unable to read scrty file. {ex.Message}");
                _scrty = null;
            }
        }

        private void SwitchChecksums()
        {
            _packet.GreenSeed = _prng.Get32();
            _packet.DoorSeed = _prng.Get32();
            _packet.Timestamp = ServerTick.Now;

            if (_scrty != null)
            {
                int i = _prng.Number(1, _scrty.Length / 2 - 1) * 2;
                _packet.Key = _scrty[i];
                _continuumExeChecksum = _scrty[i + 1];
            }
            else
            {
                _packet.Key = _prng.Get32();
                _continuumExeChecksum = 0;
            }

            // calculate new checksums
            _arenaManager.Lock();

            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                        continue;

                    if (arena.Status == ArenaState.Running)
                    {
                        ad.MapChecksum = _mapData.GetChecksum(arena, _packet.Key);
                    }
                    else
                    {
                        ad.MapChecksum = 0;
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            _vieExeChecksum = GetVieExeChecksum(_packet.Key);

            SecuritySeedChangedCallback.Fire(_broker, _packet.GreenSeed, _packet.DoorSeed, _packet.Timestamp);
        }

        // straight from ASSS, dont know what's going on with all the magic numbers
        private static uint GetVieExeChecksum(uint key)
        {
            uint part, sum = 0;

            part = 0xc98ed41f;
            part += 0x3e1bc | key;
            part ^= 0x42435942 ^ key;
            part += 0x1d895300 | key;
            part ^= 0x6b5c4032 ^ key;
            part += 0x467e44 | key;
            part ^= 0x516c7eda ^ key;
            part += 0x8b0c708b | key;
            part ^= 0x6b3e3429 ^ key;
            part += 0x560674c9 | key;
            part ^= 0xf4e6b721 ^ key;
            part += 0xe90cc483 | key;
            part ^= 0x80ece15a ^ key;
            part += 0x728bce33 | key;
            part ^= 0x1fc5d1e6 ^ key;
            part += 0x8b0c518b | key;
            part ^= 0x24f1a96e ^ key;
            part += 0x30ae0c1 | key;
            part ^= 0x8858741b ^ key;
            sum += part;

            part = 0x9c15857d;
            part += 0x424448b | key;
            part ^= 0xcd0455ee ^ key;
            part += 0x727 | key;
            part ^= 0x8d7f29cd ^ key;
            sum += part;

            part = 0x824b9278;
            part += 0x6590 | key;
            part ^= 0x8e16169a ^ key;
            part += 0x8b524914 | key;
            part ^= 0x82dce03a ^ key;
            part += 0xfa83d733 | key;
            part ^= 0xb0955349 ^ key;
            part += 0xe8000003 | key;
            part ^= 0x7cfe3604 ^ key;
            sum += part;

            part = 0xe3f8d2af;
            part += 0x2de85024 | key;
            part ^= 0xbed0296b ^ key;
            part += 0x587501f8 | key;
            part ^= 0xada70f65 ^ key;
            sum += part;

            part = 0xcb54d8a0;
            part += 0xf000001 | key;
            part ^= 0x330f19ff ^ key;
            part += 0x909090c3 | key;
            part ^= 0xd20f9f9f ^ key;
            part += 0x53004add | key;
            part ^= 0x5d81256b ^ key;
            part += 0x8b004b65 | key;
            part ^= 0xa5312749 ^ key;
            part += 0xb8004b67 | key;
            part ^= 0x8adf8fb1 ^ key;
            part += 0x8901e283 | key;
            part ^= 0x8ec94507 ^ key;
            part += 0x89d23300 | key;
            part ^= 0x1ff8e1dc ^ key;
            part += 0x108a004a | key;
            part ^= 0xc73d6304 ^ key;
            part += 0x43d2d3 | key;
            part ^= 0x6f78e4ff ^ key;
            sum += part;

            part = 0x45c23f9;
            part += 0x47d86097 | key;
            part ^= 0x7cb588bd ^ key;
            part += 0x9286 | key;
            part ^= 0x21d700f8 ^ key;
            part += 0xdf8e0fd9 | key;
            part ^= 0x42796c9e ^ key;
            part += 0x8b000003 | key;
            part ^= 0x3ad32a21 ^ key;
            sum += part;

            part = 0xb229a3d0;
            part += 0x47d708 | key;
            part ^= 0x10b0a91 ^ key;
            sum += part;

            part = 0x466e55a7;
            part += 0xc7880d8b | key;
            part ^= 0x44ce7067 ^ key;
            part += 0xe4 | key;
            part ^= 0x923a6d44 ^ key;
            part += 0x640047d6 | key;
            part ^= 0xa62d606c ^ key;
            part += 0x2bd1f7ae | key;
            part ^= 0x2f5621fb ^ key;
            part += 0x8b0f74ff | key;
            part ^= 0x2928b332;
            sum += part;

            sum += 0x62cf369a;

            return sum;
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            lock (_lockObj)
            {
                if (action == PlayerAction.EnterArena)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                        return;

                    bool isOverride = ad.OverridePacket != null;
                    S2C_Security toSend = isOverride ? ad.OverridePacket.Value : _packet;
                    toSend.Key = 0; // no key

                    _logManager.LogP(LogLevel.Drivel, nameof(Security), player,
                        $"Sent seeds{(isOverride ? " (override)" : "")}: green={toSend.GreenSeed:X}, door={toSend.DoorSeed:X}, timestamp={toSend.Timestamp:X}.");

                    // Send the packet without a key (just syncing the client, not requesting the client to respond).
                    _network.SendToOne(player, ref toSend, NetSendFlags.Reliable);
                }
                else if (action == PlayerAction.LeaveArena)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData pd))
                    {
                        if (pd.Sent)
                        {
                            pd.Sent = false;
                            pd.Cancelled = true;
                        }
                    }
                }
            }
        }

        private bool MainloopTimer_Send(SendTimerData sendTimerData)
        {
            HashSet<Player> sendPlayerSet = sendTimerData.SendPlayerSet;

            SwitchChecksums();

            sendPlayerSet.Clear();

            lock (_lockObj)
            {
                //
                // Determine which players to check/sync
                //

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        // TODO: could check, but would need to send the overriden seeds along with the key
                        if (p.Arena == null || !p.Arena.TryGetExtraData(_adKey, out ArenaData ad) || ad.OverridePacket != null) // don't do a check for arenas that have an override
                            continue;

                        if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                            continue;

                        if (p.Status == PlayerState.Playing
                            && p.IsStandard
                            && p.Flags.SentPositionPacket) // having sent a position packet means the player has the map and settings
                        {
                            sendPlayerSet.Add(p);
                            pd.SettingsChecksum = _clientSettings.GetChecksum(p, _packet.Key);
                            pd.Sent = true;
                            pd.Cancelled = false;
                        }
                        else
                        {
                            pd.Sent = false;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                //
                // Send the requests
                //

                _network.SendToSet(sendPlayerSet, ref _packet, NetSendFlags.Reliable);
            }

            _logManager.LogM(LogLevel.Drivel, nameof(Security),
                $"Sent security packet to {sendPlayerSet.Count} players: green={_packet.GreenSeed:X}, door={_packet.DoorSeed:X}, timestamp={_packet.Timestamp:X}.");

            sendPlayerSet.Clear();

            // Set a timer to check in 15 seconds.
            _mainloopTimer.SetTimer(MainloopTimer_Check, 15000, Timeout.Infinite, null);

            return true;
        }

        private bool MainloopTimer_Check()
        {
            lock (_lockObj)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                            continue;

                        if (pd.Sent)
                        {
                            // Did not get a response to the security packet we sent.
                            if (_capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                            {
                                _logManager.LogP(LogLevel.Malicious, nameof(Security), p, "No security packet response.");
                            }

                            KickPlayer(p);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            return false; // don't run again
        }

        private void KickPlayer(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (_securityKickoff)
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.BypassSecurity))
                {
                    _logManager.LogP(LogLevel.Info, nameof(Security), player, "Kicking off for security violation.");
                    _playerData.KickPlayer(player);
                }
            }
        }

        private void Packet_SecurityResponse(Player player, Span<byte> data, int length, NetReceiveFlags flags)
        {
            if (player == null)
                return;

            if (length < 0 || length < C2S_Security.Length)
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Security), player, $"Got a security response with a bad packet length={length}.");
                }

                return;
            }

            Arena arena = player.Arena;

            if (arena == null)
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Security), player, "Got a security response, but is not in an arena.");
                }

                return;
            }

            _logManager.LogP(LogLevel.Drivel, nameof(Security), player, "Got a security response.");

            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            ref C2S_Security pkt = ref MemoryMarshal.AsRef<C2S_Security>(data[..length]);

            lock (_lockObj)
            {
                if (!pd.Sent)
                {
                    if (pd.Cancelled)
                    {
                        pd.Cancelled = false;
                    }
                    else
                    {
                        if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                        {
                            _logManager.LogP(LogLevel.Malicious, nameof(Security), player, "Got a security response, but wasn't expecting one.");
                        }
                    }
                }
                else
                {
                    pd.Sent = false;

                    bool kick = false;

                    if (pd.SettingsChecksum != 0 && pkt.SettingChecksum != pd.SettingsChecksum)
                    {
                        if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                        {
                            _logManager.LogP(LogLevel.Malicious, nameof(Security), player, "Settings checksum mismatch.");
                        }

                        kick = true;
                    }

                    if (ad.MapChecksum != 0 && pkt.MapChecksum != ad.MapChecksum)
                    {
                        if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                        {
                            _logManager.LogP(LogLevel.Malicious, nameof(Security), player, "Map checksum mismatch.");
                        }

                        kick = true;
                    }

                    bool exeOk = false;

                    if (player.Type == ClientType.VIE)
                    {
                        if (_vieExeChecksum == pkt.ExeChecksum)
                        {
                            exeOk = true;
                        }
                    }
                    else if (player.Type == ClientType.Continuum)
                    {
                        if (_continuumExeChecksum != 0)
                        {
                            if (_continuumExeChecksum == pkt.ExeChecksum)
                            {
                                exeOk = true;
                            }
                        }
                        else
                        {
                            exeOk = true;
                        }
                    }
                    else
                    {
                        exeOk = true;
                    }

                    if (!exeOk)
                    {
                        if (!_capabilityManager.HasCapability(player, Constants.Capabilities.SuppressSecurity))
                        {
                            _logManager.LogP(LogLevel.Malicious, nameof(Security), player, "Exe checksum mismatch.");
                        }

                        kick = true;
                    }

                    if (kick)
                    {
                        KickPlayer(player);
                    }
                }
            }

            // submit info to the lag data collector
            ClientLatencyData cld = new()
            {
                WeaponCount = pkt.WeaponCount,
                S2CSlowTotal = pkt.S2CSlowTotal,
                S2CFastTotal = pkt.S2CFastTotal,
                S2CSlowCurrent = pkt.S2CSlowCurrent,
                S2CFastCurrent = pkt.S2CFastCurrent,
                Unknown1 = pkt.Unknown1,
                LastPing = pkt.LastPing,
                AveragePing = pkt.AveragePing,
                LowestPing = pkt.LowestPing,
                HighestPing = pkt.HighestPing,
            };

            _lagCollect.ClientLatency(player, in cld);
        }

        /// <summary>
        /// Per arena data
        /// </summary>
        private class ArenaData : IResettable
        {
            /// <summary>
            /// Shared checksums
            /// </summary>
            public uint MapChecksum;

            /// <summary>
            /// The S2C security packet for when seeds are overridden.
            /// E.g., during playback of a replay to synchronize door timings to match what was recorded.
            /// </summary>
            public S2C_Security? OverridePacket;

			public bool TryReset()
			{
                MapChecksum = 0;
                OverridePacket = null;
                return true;
            }
        }

        /// <summary>
        /// Per player data
        /// </summary>
        private class PlayerData : IResettable
        {
            /// <summary>
            /// Whether a security request was sent and is still pending (hasn't been fulfilled with a valid response yet).
            /// </summary>
            public bool Sent;

            /// <summary>
            /// Whether to consider a request as cancelled due to the player having changed arenas mid-request/response.
            /// Changing arenas means map and settings checksums will likely change too.
            /// </summary>
            public bool Cancelled;

            /// <summary>
            /// individual checksums
            /// </summary>
            public uint SettingsChecksum;

            public bool TryReset()
            {
                Sent = false;
                Cancelled = false;
                SettingsChecksum = 0;
                return true;
            }
        }

        /// <summary>
        /// Timer local data.
        /// To reuse objects without having to reallocate on each iteration of the timer.
        /// </summary>
        private class SendTimerData
        {
            public readonly HashSet<Player> SendPlayerSet = new(256);
        }
    }
}
