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
        private IMainloopTimer _mainloopTimer;
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
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            INetwork network)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
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

            if (ad.Flags == null)
                return;

            DateTime now = DateTime.UtcNow;

            for (int i = 0; i < ad.Flags.Length; i++)
            {
                ref FlagData flagData = ref ad.Flags[i];
                flagData.OwnerFreq = -1; // not owned;
                flagData.DirtyPlayer = null;
                flagData.LastSendTimestamp = now;
            }

            SendFullFlagUpdate(arena);

            FlagGameResetCallback.Fire(arena, arena, -1, 0);
        }

        int IFlagGame.GetFlagCount(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.Flags == null)
                return 0;

            return ad.Flags.Length;
        }

        int IFlagGame.GetFlagCount(Arena arena, int freq)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.Flags == null)
                return 0;

            int count = 0;
            for (int i = 0; i < ad.Flags.Length; i++)
                if (ad.Flags[i].OwnerFreq == freq)
                    count++;

            return count;
        }

        bool IStaticFlagGame.TryGetFlagOwners(Arena arena, Span<short> owners)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.Flags == null
                || ad.Flags.Length != owners.Length)
            {
                return false;
            }

            for (int i = 0; i < ad.Flags.Length; i++)
                owners[i] = ad.Flags[i].OwnerFreq;

            return true;
        }

        bool IStaticFlagGame.SetFlagOwners(Arena arena, ReadOnlySpan<short> flagOwners)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.Flags == null)
                return false;

            if (ad.Flags.Length != flagOwners.Length)
                return false;

            DateTime now = DateTime.UtcNow;

            for (int i = 0; i < ad.Flags.Length; i++)
            {
                ref FlagData flagData = ref ad.Flags[i];
                flagData.OwnerFreq = flagOwners[i];
                flagData.DirtyPlayer = null;
                flagData.LastSendTimestamp = now;
            }

            SendFullFlagUpdate(arena);

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

            if (ad.Flags == null)
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
            if (flagId < 0 || flagId >= ad.Flags.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(StaticFlags), p, $"C2S_TouchFlag packet - bad flag id.");
                return;
            }

            ref FlagData flagData = ref ad.Flags[flagId];

            short oldFreq = flagData.OwnerFreq;
            short newFreq = p.Freq;

            if (oldFreq == newFreq)
                return; // no change

            flagData.OwnerFreq = newFreq;
            flagData.DirtyPlayer = p;

            TrySendSingleFlagUpdateToArena(arena, ad, flagId, ref flagData, DateTime.UtcNow);

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

                    DateTime now = DateTime.UtcNow;

                    ad.Flags = new FlagData[numFlags];
                    for (int i = 0; i < numFlags; i++)
                    {
                        ref FlagData flagData = ref ad.Flags[i];
                        flagData.OwnerFreq = -1; // not owned
                        flagData.DirtyPlayer = null;
                        flagData.LastSendTimestamp = now;
                    }

                    if (ad.FlagGameRegistrationToken == null)
                        ad.FlagGameRegistrationToken = arena.RegisterInterface<IFlagGame>(this);

                    if (ad.StaticFlagGameRegistrationToken == null)
                        ad.StaticFlagGameRegistrationToken = arena.RegisterInterface<IStaticFlagGame>(this);

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SendFlagUpdates, arena);
                    _mainloopTimer.SetTimer(MainloopTimer_SendFlagUpdates, 500, 500, arena, arena);
                }
                else
                {
                    ad.IsPersistEnabled = false;
                    ad.Flags = null;

                    if (ad.FlagGameRegistrationToken != null)
                        arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);

                    if (ad.StaticFlagGameRegistrationToken != null)
                        arena.UnregisterInterface(ref ad.StaticFlagGameRegistrationToken);

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SendFlagUpdates, arena);

                    // TODO: does sending an empty, 1 byte 0x22 tell the client to remove the flags?
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                if (ad.FlagGameRegistrationToken != null)
                    arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);

                if (ad.StaticFlagGameRegistrationToken != null)
                    arena.UnregisterInterface(ref ad.StaticFlagGameRegistrationToken);

                _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SendFlagUpdates, arena);
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                SendFullFlagUpdate(p);
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData ad)
                    || ad.Flags == null)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;

                // make sure any pending flag updates are sent before the player leaves
                for (short flagId = 0; flagId < ad.Flags.Length; flagId++)
                {
                    ref FlagData flagData = ref ad.Flags[flagId];

                    if (flagData.DirtyPlayer == p)
                    {
                        SendSingleFlagUpdateToArena(arena, flagId, ref flagData, now);
                    }
                }
            }
        }

        #endregion

        #region Persist methods

        private void Persist_GetOwners(Arena arena, Stream outStream)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!ad.IsPersistEnabled || ad.Flags == null)
                return;

            StaticFlagsData staticFlagsData = new();
            staticFlagsData.MapChecksum = _mapData.GetChecksum(arena, 0);

            for (int i = 0; i < ad.Flags.Length; i++)
                staticFlagsData.OwnerFreqs.Add(ad.Flags[i].OwnerFreq);

            staticFlagsData.WriteTo(outStream);
        }

        private void Persist_SetOwners(Arena arena, Stream inStream)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!ad.IsPersistEnabled || ad.Flags == null)
                return;

            StaticFlagsData staticFlagsData = StaticFlagsData.Parser.ParseFrom(inStream);
            uint checksum = _mapData.GetChecksum(arena, 0);
            if (staticFlagsData.MapChecksum != checksum)
            {
                _logManager.LogM(LogLevel.Info, nameof(StaticFlags), $"Map checksum of persisted data ({staticFlagsData.MapChecksum} does not match the current map ({checksum}).");
                return;
            }

            if (staticFlagsData.OwnerFreqs.Count != ad.Flags.Length)
            {
                _logManager.LogM(LogLevel.Info, nameof(StaticFlags), $"# of flags in persisted data ({staticFlagsData.OwnerFreqs.Count} does not the current map ({ad.Flags.Length}).");
                return;
            }

            DateTime now = DateTime.UtcNow;

            for (int i = 0; i < ad.Flags.Length; i++)
            {
                ref FlagData flagData = ref ad.Flags[i];
                flagData.OwnerFreq = (short)staticFlagsData.OwnerFreqs[i];
                flagData.DirtyPlayer = null;
                flagData.LastSendTimestamp = now;
            }
        }

        #endregion

        private bool MainloopTimer_SendFlagUpdates(Arena arena)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.Flags == null)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;

            for (short flagId = 0; flagId < ad.Flags.Length; flagId++)
            {
                ref FlagData flagData = ref ad.Flags[flagId];
                TrySendSingleFlagUpdateToArena(arena, ad, flagId, ref flagData, now);
            }

            return true;
        }

        private bool TrySendSingleFlagUpdateToArena(Arena arena, ArenaData ad, short flagId, ref FlagData flagData, DateTime now)
        {
            if (arena != null
                && ad != null
                && flagData.DirtyPlayer != null
                && (now - flagData.LastSendTimestamp) > ad.SendFlagUpdateCooldown)
            {
                SendSingleFlagUpdateToArena(arena, flagId, ref flagData, now);
                return true;
            }
            else
            {
                return false;
            }
        }

        private void SendSingleFlagUpdateToArena(Arena arena, short flagId, ref FlagData flagData, DateTime now)
        {
            if (arena == null)
                return;

            S2C_FlagPickup s2c = new(flagId, (short)flagData.DirtyPlayer.Id);
            _network.SendToArena(arena, null, ref s2c, NetSendFlags.Reliable);

            flagData.DirtyPlayer = null;
            flagData.LastSendTimestamp = now;
        }

        private void SendFullFlagUpdate(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.Flags == null)
                return;

            Span<byte> packet = stackalloc byte[1 + (ad.Flags.Length * 2)];
            packet[0] = (byte)S2CPacketType.TurfFlags;

            Span<short> flagOwners = MemoryMarshal.Cast<byte, short>(packet[1..]);
            for (int i = 0; i < ad.Flags.Length; i++)
                flagOwners[i] = ad.Flags[i].OwnerFreq;

            _network.SendToOne(player, packet, NetSendFlags.Reliable);
        }

        private void SendFullFlagUpdate(Arena arena)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.Flags == null)
                return;

            Span<byte> packet = stackalloc byte[1 + (ad.Flags.Length * 2)];
            packet[0] = (byte)S2CPacketType.TurfFlags;

            Span<short> flagOwners = MemoryMarshal.Cast<byte, short>(packet[1..]);
            for (int i = 0; i < ad.Flags.Length; i++)
                flagOwners[i] = ad.Flags[i].OwnerFreq;

            _network.SendToArena(arena, null, packet, NetSendFlags.Reliable);
        }

        #region Helper types

        private struct FlagData
        {
            /// <summary>
            /// The team that owns the flag.
            /// </summary>
            public short OwnerFreq;

            /// <summary>
            /// The player that last claimed the flag and is "dirty", meaning a flag update needs to be sent to the arena.
            /// </summary>
            /// <remarks>
            /// We keep track of the player rather than just a dirty flag since the packet requires the player's id.
            /// </remarks>
            public Player DirtyPlayer;

            /// <summary>
            /// Timestamp a flag update was last sent for the flag.
            /// </summary>
            /// <remarks>
            /// Used to prevent flooding of flag update packets. For example, when players on different teams are standing on top of the same flag.
            /// </remarks>
            public DateTime LastSendTimestamp;
        }

        private class ArenaData
        {
            public InterfaceRegistrationToken<IFlagGame> FlagGameRegistrationToken;
            public InterfaceRegistrationToken<IStaticFlagGame> StaticFlagGameRegistrationToken;

            public bool IsPersistEnabled = false;
            public readonly TimeSpan SendFlagUpdateCooldown = TimeSpan.FromMilliseconds(500); // TODO: make this configurable?
            public FlagData[] Flags = null;
        }

        #endregion
    }
}
