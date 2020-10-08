using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class Security : IModule
    {
        private IArenaManager arenaManager;
        private ICapabilityManager capabilityManager;
        private IClientSettings clientSettings;
        private IConfigManager configManager;
        private ILogManager logManager;
        private IMainloopTimer mainloopTimer;
        private IMapData mapData;
        private INetwork network;
        private IPlayerData playerData;
        private IPrng prng;

        private int adKey;
        private int pdKey;

        private const int ScrtyLength = 1000;
        private uint[] scrty;

        private S2CSecurity packet;

        private uint continuumExeChecksum;
        private uint vieExeChecksum;
        private readonly object lockObj = new object();

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

            mainloopTimer.SetTimer(MainloopTimer_Send, 2500, 6000, null);

            network.AddPacket((int)C2SPacketType.SecurityResponse, Packet_SecurityResponse);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            network.RemovePacket((int)C2SPacketType.SecurityResponse, Packet_SecurityResponse);
            mainloopTimer.ClearTimer(MainloopTimer_Send, null);
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
                Span<byte> scrtyByteSpan = MemoryMarshal.Cast<uint, byte>(scrty);
                int bytesRead = fs.Read(scrtyByteSpan);
                if (bytesRead != ScrtyLength * sizeof(uint))
                {
                    throw new Exception($"Expected to read {ScrtyLength * sizeof(uint)} bytes but only got {bytesRead} bytes.");
                }

                /*
                // TODO: a version that would work on big endian architecture too
                using FileStream fs = File.OpenRead("scrty");
                Span<byte> data = stackalloc byte[4];
                for (int i = 0; i < ScrtyLength; i++)
                {
                    int numBytes = fs.Read(data);
                    if (numBytes != 4)
                        throw new Exception($"Expected to read 4 bytes but only got {numBytes} bytes.");

                    //scrty[i] = LittleEndianBitConverter.ToUInt32(data); // TODO: need to implement an overload that takes Span<byte>
                }
                */
            }
            catch (Exception ex)
            {
                logManager.LogM(LogLevel.Info, nameof(Security), "Unable to read scrty file. {0}", ex.Message);
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
                foreach (Arena arena in arenaManager.ArenaList)
                {
                    if (!(arena[adKey] is ArenaData ad))
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
        private uint GetVieExeChecksum(uint key)
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
                        "send seeds: green={0:X}, door={1:X}, timestamp={2:X}",
                        packet.GreenSeed, packet.DoorSeed, packet.Timestamp);

                    uint key = packet.Key;
                    packet.Key = 0;

                    Span<byte> packetSpan = MemoryMarshal.Cast<S2CSecurity, byte>(MemoryMarshal.CreateSpan(ref packet, 1));
                    network.SendToOne(p, packetSpan, NetSendFlags.Reliable);

                    packet.Key = key;
                }
                else if (action == PlayerAction.LeaveArena)
                {
                    if ((p[pdKey] is PlayerData pd))
                    {
                        pd.Sent = false;
                    }
                }
            }
        }

        private bool MainloopTimer_Send()
        {
            // TODO:
            return true;
        }

        private bool MainloopTimer_Check()
        {
            // TODO:
            return true;
        }

        private void Packet_SecurityResponse(Player p, byte[] data, int length)
        {
            // TODO:
            logManager.LogP(LogLevel.Drivel, nameof(Security), p, $"received C2S security response of length {length}");
        }

        private class ArenaData
        {
            /// <summary>
            /// Shared checksums
            /// </summary>
            public uint MapChecksum;
        }

        private class PlayerData
        {
            /// <summary>
            /// whether we went a security request or not
            /// </summary>
            public bool Sent;

            /// <summary>
            /// individual checksums
            /// </summary>
            public uint SettingsChecksum;
        }
    }
}
