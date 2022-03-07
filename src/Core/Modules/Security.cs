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
    public class Security : IModule
    {
        private IArenaManager arenaManager;
        private ICapabilityManager capabilityManager;
        private IClientSettings clientSettings;
        private IConfigManager configManager;
        private ILagCollect lagCollect;
        private ILogManager logManager;
        private IMainloopTimer mainloopTimer;
        private IMapData mapData;
        private INetwork network;
        private IPlayerData playerData;
        private IPrng prng;

        /// <summary>
        /// Arena data key for accessing <see cref="ArenaData"/>.
        /// </summary>
        private int adKey;

        /// <summary>
        /// Player data key for accessing <see cref="PlayerData"/>.
        /// </summary>
        private PlayerDataKey pdKey;

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
        private uint[] scrty;

        /// <summary>
        /// The packet to send to players.
        /// </summary>
        private S2C_Security packet;

        /// <summary>
        /// The continuum exe checksum from <see cref="scrty"/>.
        /// </summary>
        private uint continuumExeChecksum;

        /// <summary>
        /// The VIE exe checksum. See <see cref="GetVieExeChecksum(uint)"/>.
        /// </summary>
        private uint vieExeChecksum;

        /// <summary>
        /// For synchronizing access to this class' data.
        /// </summary>
        private readonly object lockObj = new();

        [ConfigHelp("Security", "SecurityKickoff", ConfigScope.Global, typeof(bool), DefaultValue = "false", 
            Description = "Whether to kick players off of the server for violating security checks.")]
        private bool cfg_SecurityKickoff;

        public Security()
        {
            packet.Type = (byte)S2CPacketType.Security;
        }

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
            this.arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            this.capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            this.clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            this.configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            this.lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            this.logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            this.mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            this.mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            this.network = network ?? throw new ArgumentNullException(nameof(network));
            this.playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            this.prng = prng ?? throw new ArgumentNullException(nameof(prng));

            adKey = arenaManager.AllocateArenaData<ArenaData>();
            pdKey = playerData.AllocatePlayerData<PlayerData>();

            LoadScrty();

            cfg_SecurityKickoff = configManager.GetInt(configManager.Global, "Security", "SecurityKickoff", 0) != 0;

            SwitchChecksums();

            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            mainloopTimer.SetTimer(MainloopTimer_Send, 25000, 60000, new SendTimerData(),null);

            network.AddPacket(C2SPacketType.SecurityResponse, Packet_SecurityResponse);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            network.RemovePacket(C2SPacketType.SecurityResponse, Packet_SecurityResponse);
            mainloopTimer.ClearTimer<SendTimerData>(MainloopTimer_Send, null);
            mainloopTimer.ClearTimer(MainloopTimer_Check, null);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            arenaManager.FreeArenaData(adKey);
            playerData.FreePlayerData(pdKey);

            return true;
        }

        private void LoadScrty()
        {
            try
            {
                scrty = new uint[ScrtyLength];

                using FileStream fs = File.OpenRead("scrty");
                using BinaryReader br = new(fs);

                for (int i = 0; i < ScrtyLength; i++)
                {
                    scrty[i] = br.ReadUInt32(); // reads bytes as little-endian
                }
            }
            catch (Exception ex)
            {
                logManager.LogM(LogLevel.Info, nameof(Security), $"Unable to read scrty file. {ex.Message}");
                scrty = null;
            }
        }

        private void SwitchChecksums()
        {
            packet.GreenSeed = prng.Get32();
            packet.DoorSeed = prng.Get32();
            packet.Timestamp = ServerTick.Now;

            if (scrty != null)
            {
                int i = prng.Number(1, scrty.Length / 2 - 1) * 2;
                packet.Key = scrty[i];
                continuumExeChecksum = scrty[i + 1];
            }
            else
            {
                packet.Key = prng.Get32();
                continuumExeChecksum = 0;
            }

            // calculate new checksums
            arenaManager.Lock();

            try
            {
                foreach (Arena arena in arenaManager.Arenas)
                {
                    if (arena[adKey] is not ArenaData ad)
                        continue;

                    if (arena.Status == ArenaState.Running)
                    {
                        ad.MapChecksum = mapData.GetChecksum(arena, packet.Key);
                    }
                    else
                    {
                        ad.MapChecksum = 0;
                    }
                }
            }
            finally
            {
                arenaManager.Unlock();
            }

            vieExeChecksum = GetVieExeChecksum(packet.Key);
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

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            lock (lockObj)
            {
                if (action == PlayerAction.EnterArena)
                {
                    logManager.LogP(LogLevel.Drivel, nameof(Security), p,
                        $"Send seeds: green={packet.GreenSeed:X}, door={packet.DoorSeed:X}, timestamp={packet.Timestamp:X}.");
                    
                    uint key = packet.Key;
                    packet.Key = 0;

                    // Send the packet without a key (just syncing the client, not requesting the client to respond).
                    network.SendToOne(p, ref packet, NetSendFlags.Reliable);

                    packet.Key = key;
                }
                else if (action == PlayerAction.LeaveArena)
                {
                    if ((p[pdKey] is PlayerData pd))
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

            lock (lockObj)
            {
                //
                // Determine which players to check/sync
                //

                playerData.Lock();

                try
                {
                    foreach (Player p in playerData.Players)
                    {
                        if (p[pdKey] is not PlayerData pd)
                            continue;

                        if (p.Status == PlayerState.Playing
                            && p.IsStandard
                            && p.Flags.SentPositionPacket) // having sent a position packet means the player has the map and settings
                        {
                            sendPlayerSet.Add(p);
                            pd.SettingsChecksum = clientSettings.GetChecksum(p, packet.Key);
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
                    playerData.Unlock();
                }

                //
                // Send the requests
                //

                network.SendToSet(sendPlayerSet, ref packet, NetSendFlags.Reliable);
            }

            logManager.LogM(LogLevel.Drivel, nameof(Security),
                $"Sent security packet to {sendPlayerSet.Count} players: green={packet.GreenSeed:X}, door={packet.DoorSeed:X}, timestamp={packet.Timestamp:X}.");

            sendPlayerSet.Clear();

            // Set a timer to check in 15 seconds.
            mainloopTimer.SetTimer(MainloopTimer_Check, 15000, Timeout.Infinite, null);

            return true;
        }

        private bool MainloopTimer_Check()
        {
            lock (lockObj)
            {
                playerData.Lock();

                try
                {
                    foreach (Player p in playerData.Players)
                    {
                        if (p[pdKey] is not PlayerData pd)
                            continue;

                        if (pd.Sent)
                        {
                            // Did not get a response to the security packet we sent.
                            if (capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                            {
                                logManager.LogP(LogLevel.Malicious, nameof(Security), p, "No security packet response.");
                            }

                            KickPlayer(p);
                        }
                    }
                }
                finally
                {
                    playerData.Unlock();
                }
            }

            return false; // don't run again
        }

        private void KickPlayer(Player p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (cfg_SecurityKickoff)
            {
                if (!capabilityManager.HasCapability(p, Constants.Capabilities.BypassSecurity))
                {
                    logManager.LogP(LogLevel.Info, nameof(Security), p, "Kicking off for security violation.");
                    playerData.KickPlayer(p);
                }
            }
        }

        private void Packet_SecurityResponse(Player p, byte[] data, int length)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (length < 0 || length < C2S_Security.Length)
            {
                if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                {
                    logManager.LogP(LogLevel.Malicious, nameof(Security), p, $"Got a security response with a bad packet length={length}.");
                }

                return;
            }

            Arena arena = p.Arena;

            if (arena == null)
            {
                if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                {
                    logManager.LogP(LogLevel.Malicious, nameof(Security), p, "Got a security response, but is not in an arena.");
                }

                return;
            }

            logManager.LogP(LogLevel.Drivel, nameof(Security), p, "Got a security response.");

            if (p[pdKey] is not PlayerData pd)
                return;

            if (arena[adKey] is not ArenaData ad)
                return;

            ref C2S_Security pkt = ref MemoryMarshal.AsRef<C2S_Security>(new Span<byte>(data, 0, length));

            lock (lockObj)
            {
                if (!pd.Sent)
                {
                    if (pd.Cancelled)
                    {
                        pd.Cancelled = false;
                    }
                    else
                    {
                        if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                        {
                            logManager.LogP(LogLevel.Malicious, nameof(Security), p, "Got a security response, but wasn't expecting one.");
                        }
                    }
                }
                else
                {
                    pd.Sent = false;

                    bool kick = false;

                    if (pd.SettingsChecksum != 0 && pkt.SettingChecksum != pd.SettingsChecksum)
                    {
                        if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                        {
                            logManager.LogP(LogLevel.Malicious, nameof(Security), p, "Settings checksum mismatch.");
                        }

                        kick = true;
                    }

                    if (ad.MapChecksum != 0 && pkt.MapChecksum != ad.MapChecksum)
                    {
                        if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                        {
                            logManager.LogP(LogLevel.Malicious, nameof(Security), p, "Map checksum mismatch.");
                        }

                        kick = true;
                    }

                    bool exeOk = false;

                    if (p.Type == ClientType.VIE)
                    {
                        if (vieExeChecksum == pkt.ExeChecksum)
                        {
                            exeOk = true;
                        }
                    }
                    else if (p.Type == ClientType.Continuum)
                    {
                        if (continuumExeChecksum != 0)
                        {
                            if (continuumExeChecksum == pkt.ExeChecksum)
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
                        if (!capabilityManager.HasCapability(p, Constants.Capabilities.SuppressSecurity))
                        {
                            logManager.LogP(LogLevel.Malicious, nameof(Security), p, "Exe checksum mismatch.");
                        }

                        kick = true;
                    }

                    if (kick)
                    {
                        KickPlayer(p);
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

            lagCollect.ClientLatency(p, in cld);
        }

        /// <summary>
        /// Per arena data
        /// </summary>
        private class ArenaData
        {
            /// <summary>
            /// Shared checksums
            /// </summary>
            public uint MapChecksum;
        }

        /// <summary>
        /// Per player data
        /// </summary>
        private class PlayerData
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
