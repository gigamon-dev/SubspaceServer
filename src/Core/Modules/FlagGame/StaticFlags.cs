using Google.Protobuf;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Persist.Protobuf;
using SS.Packets.Game;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SS.Core.Modules.FlagGame
{
    /// <summary>
    /// Module that manages flag games where the flags are static (based on where flags are placed on the map).
    /// In other words, flags that can't be carried, AKA "turf" style flags.
    /// </summary>
    public class StaticFlags : IModule, IStaticFlagGame
    {
        /// <summary>
        /// The maximum # of static flags allowed.
        /// </summary>
        /// <remarks>
        /// The protocol limits how many can fit into a packet.
        /// A 0x22 packet has a max size of 513 bytes.
        /// </remarks>
        private const int MaxFlags = 256;

        // required dependencies
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private INetwork _network;
        
        // optional dependencies
        private IPersist _persist;

        private ArenaDataKey<ArenaData> _adKey;

        private DelegatePersistentData<Arena> _persistRegistration;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMapData mapData,
            INetwork network)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _persist = broker.GetInterface<IPersist>();
            if (_persist != null)
            {
                _persistRegistration = new(
                    (int)PersistKey.StaticFlags, PersistInterval.ForeverNotShared, PersistScope.PerArena, Persist_GetOwners, Persist_SetOwners, null);

                _persist.RegisterPersistentData(_persistRegistration);
            }

            _network.AddPacket(C2SPacketType.TouchFlag, Packet_TouchFlag);

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _network.RemovePacket(C2SPacketType.TouchFlag, Packet_TouchFlag);

            if (_persist != null)
            {
                if (_persistRegistration != null)
                    _persist.UnregisterPersistentData(_persistRegistration);

                broker.ReleaseInterface(ref _persist);
            }   

            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region IFlagGame, IStaticFlagGame

        void IFlagGame.ResetGame(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagOwners == null)
                return;

            for (int i = 0; i < ad.FlagOwners.Length; i++)
                ad.FlagOwners[i] = -1; // not owned

            SendStaticFlagPacket(arena);

            FlagGameResetCallback.Fire(arena, arena);
        }

        int IFlagGame.GetFlagCount(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.FlagOwners == null)
                return 0;

            return ad.FlagOwners.Length;
        }

        int IFlagGame.GetFlagCount(Arena arena, int freq)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.FlagOwners == null)
                return 0;

            int count = 0;
            for (int i = 0; i < ad.FlagOwners.Length; i++)
                if (ad.FlagOwners[i] == freq)
                    count++;

            return count;
        }

        ReadOnlySpan<short> IStaticFlagGame.GetFlagOwners(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return ReadOnlySpan<short>.Empty;

            return ad.FlagOwners;
        }

        bool IStaticFlagGame.SetFlagOwners(Arena arena, ReadOnlySpan<short> flagOwners)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.FlagOwners == null)
                return false;

            if (ad.FlagOwners.Length != flagOwners.Length)
                return false;

            for (int i = 0; i < ad.FlagOwners.Length; i++)
                ad.FlagOwners[i] = flagOwners[i];

            SendStaticFlagPacket(arena);

            return true;
        }

        #endregion

        #region Packet handlers

        private void Packet_TouchFlag(Player p, byte[] data, int length)
        {
            if (length != C2S_TouchFlag.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"Invalid C2S_TouchFlag packet length ({length}).");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"C2S_TouchFlag packet but not in an arena.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagOwners == null)
                return; // module not enabled for the arena, there could be another flag game module that will handle it

            if (p.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"C2S_TouchFlag packet but not in playing state.");
                return;
            }

            if (p.Ship == ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(StaticFlags), p, $"C2S_TouchFlag packet from spec.");
                return;
            }

            if (p.Flags.DuringChange)
            {
                _logManager.LogP(LogLevel.Info, nameof(StaticFlags), p, $"C2S_TouchFlag packet before ack from ship/freq change.");
                return;
            }

            if (p.Flags.NoFlagsBalls)
            {
                _logManager.LogP(LogLevel.Info, nameof(StaticFlags), p, $"Too lagged to tag a flag.");
                return;
            }

            ref C2S_TouchFlag packet = ref MemoryMarshal.AsRef<C2S_TouchFlag>(data);
            if (packet.FlagId >= MaxFlags)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"C2S_TouchFlag packet but flag id >= {MaxFlags}.");
                return;
            }

            byte flagId = (byte)packet.FlagId;
            if (flagId < 0 || flagId >= ad.FlagOwners.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"C2S_TouchFlag packet - bad flag id.");
                return;
            }

            short oldFreq = ad.FlagOwners[flagId];
            short newFreq = p.Freq;

            ad.FlagOwners[flagId] = newFreq;

            StaticFlagClaimedCallback.Fire(arena, arena, p, flagId, oldFreq, newFreq);
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                int numFlags = Math.Clamp(_mapData.GetFlagCount(arena), 0, MaxFlags);

                if (_configManager.GetEnum(arena.Cfg, "Flag", "CarryFlags", (ConfigCarryFlags)(-1)) == ConfigCarryFlags.None
                    && numFlags > 0)
                {
                    ad.IsPersistEnabled = _configManager.GetInt(arena.Cfg, "Flag", "PersistentTurfOwners", 1) != 0;

                    ad.FlagOwners = new short[numFlags];
                    for (int i = 0; i < ad.FlagOwners.Length; i++)
                        ad.FlagOwners[i] = -1; // not owned

                    if (ad.FlagGameRegistrationToken == null)
                        ad.FlagGameRegistrationToken = arena.RegisterInterface<IFlagGame>(this);

                    if (ad.StaticFlagGameRegistrationToken == null)
                        ad.StaticFlagGameRegistrationToken = arena.RegisterInterface<IStaticFlagGame>(this);
                }
                else
                {
                    ad.IsPersistEnabled = false;
                    ad.FlagOwners = null;

                    if (ad.FlagGameRegistrationToken != null)
                        arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);

                    if (ad.StaticFlagGameRegistrationToken != null)
                        arena.UnregisterInterface(ref ad.StaticFlagGameRegistrationToken);

                    // TODO: does sending an empty, 1 byte 0x22 tell the client to remove the flags?
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                if (ad.FlagGameRegistrationToken != null)
                    arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);

                if (ad.StaticFlagGameRegistrationToken != null)
                    arena.UnregisterInterface(ref ad.StaticFlagGameRegistrationToken);
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                SendStaticFlagPacket(p);
            }
        }

        #endregion

        #region Persist methods

        private void Persist_GetOwners(Arena arena, Stream outStream)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!ad.IsPersistEnabled || ad.FlagOwners == null)
                return;

            StaticFlagsData staticFlagsData = new();
            staticFlagsData.MapChecksum = _mapData.GetChecksum(arena, 0);

            for (int i = 0; i < ad.FlagOwners.Length; i++)
                staticFlagsData.OwnerFreqs.Add(ad.FlagOwners[i]);

            staticFlagsData.WriteTo(outStream);
        }

        private void Persist_SetOwners(Arena arena, Stream inStream)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!ad.IsPersistEnabled || ad.FlagOwners == null)
                return;

            StaticFlagsData staticFlagsData = StaticFlagsData.Parser.ParseFrom(inStream);
            uint checksum = _mapData.GetChecksum(arena, 0);
            if (staticFlagsData.MapChecksum != checksum)
            {
                _logManager.LogM(LogLevel.Info, nameof(StaticFlags), $"Map checksum of persisted data ({staticFlagsData.MapChecksum} does not match the current map ({checksum}).");
                return;
            }

            if (staticFlagsData.OwnerFreqs.Count != ad.FlagOwners.Length)
            {
                _logManager.LogM(LogLevel.Info, nameof(StaticFlags), $"# of flags in persisted data ({staticFlagsData.OwnerFreqs.Count} does not the current map ({ad.FlagOwners.Length}).");
                return;
            }

            for (int i = 0; i < ad.FlagOwners.Length; i++)
                ad.FlagOwners[i] = (short)staticFlagsData.OwnerFreqs[i];
        }

        #endregion

        private void SendStaticFlagPacket(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagOwners == null)
                return;

            Span<byte> packet = stackalloc byte[1 + (ad.FlagOwners.Length * 2)];
            packet[0] = (byte)S2CPacketType.TurfFlags;

            Span<short> flagOwners = MemoryMarshal.Cast<byte, short>(packet[1..]);
            ad.FlagOwners.CopyTo(flagOwners);

            _network.SendToOne(player, packet, NetSendFlags.Reliable);
        }

        private void SendStaticFlagPacket(Arena arena)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagOwners == null)
                return;

            Span<byte> packet = stackalloc byte[1 + (ad.FlagOwners.Length * 2)];
            packet[0] = (byte)S2CPacketType.TurfFlags;

            Span<short> flagOwners = MemoryMarshal.Cast<byte, short>(packet[1..]);
            ad.FlagOwners.CopyTo(flagOwners);

            _network.SendToArena(arena, null, packet, NetSendFlags.Reliable);
        }

        #region Helper types

        private class ArenaData
        {
            public InterfaceRegistrationToken<IFlagGame> FlagGameRegistrationToken;
            public InterfaceRegistrationToken<IStaticFlagGame> StaticFlagGameRegistrationToken;

            public bool IsPersistEnabled = false;
            public short[] FlagOwners = null;
        }

        #endregion
    }
}
