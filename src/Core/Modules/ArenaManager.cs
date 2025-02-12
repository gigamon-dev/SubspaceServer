using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Collections;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeamSettings = SS.Core.ConfigHelp.Constants.Arena.Team;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages arenas, which includes the arena life-cycle: 
    /// the states they are in, transitions between states, movement of players between arenas, etc.
    /// </summary>
    [CoreModuleInfo]
    public sealed class ArenaManager : IModule, IArenaManager, IArenaManagerInternal, IModuleLoaderAware, IDisposable
    {
        private const string ArenasDirectoryName = "arenas";

        /// <summary>
        /// the read-write lock for the global arena list
        /// </summary>
        private readonly ReaderWriterLockSlim _arenaLock = new(LockRecursionPolicy.SupportsRecursion);

        private readonly Dictionary<string, Arena> _arenas = new(Constants.TargetArenaCount, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Arena>.AlternateLookup<ReadOnlySpan<char>> _arenasLookup;

        /// <summary>
        /// Key = module Type
        /// Value = list of arenas that have the module attached
        /// </summary>
        private readonly Dictionary<Type, List<Arena>> _attachedModules = new(64);

        internal readonly IComponentBroker Broker;

        // required dependencies
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IModuleManager _moduleManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IServerTimer _serverTimer;

        // optional dependencies
        private IChatNetwork? _chatNetwork;
        private INetwork? _network;
        private IPersistExecutor? _persistExecutor;

        private InterfaceRegistrationToken<IArenaManager>? _iArenaManagerToken;
        private InterfaceRegistrationToken<IArenaManagerInternal>? _iArenaManagerInternalToken;

        // for managing per arena data
        private readonly SortedList<int, ExtraDataFactory> _extraDataRegistrations = new(Constants.TargetArenaExtraDataCount);
        private readonly DefaultObjectPoolProvider _poolProvider = new() { MaximumRetained = Constants.TargetArenaCount };

        // population
        private int _playersTotal;
        private int _playersPlaying;
        private DateTime? _populationLastRefreshed;
        private readonly TimeSpan _populationRefreshThreshold = TimeSpan.FromMilliseconds(1000);
        private readonly ReaderWriterLockSlim _populationLock = new();

        /// <summary>
        /// Per player data key (<see cref="SpawnLoc"/>) 
        /// </summary>
        private PlayerDataKey<SpawnLoc> _spawnKey;

        /// <summary>
        /// Per arena data key (<see cref="ArenaData"/>) 
        /// </summary>
        private ArenaDataKey<ArenaData> _adKey;

        private FileSystemWatcher? _knownArenaWatcher;
        private readonly Trie _knownArenaNames = new(false);
        private readonly ReadOnlyTrie _readOnlyKnownArenaNames;

        private bool _isRefreshKnownArenasRequested = false;
        private Task? _refreshKnownArenasTask = null;
        private readonly Lock _refreshLock = new();

        // cached delegates
        private readonly ConfigChangedDelegate<Arena> _arenaConfChanged;
        private readonly Action<Arena> _arenaSyncDone;
        private readonly Action _refreshKnownArenas;

        public ArenaManager(
            IComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IModuleManager moduleManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IServerTimer serverTimer)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            _arenasLookup = _arenas.GetAlternateLookup<ReadOnlySpan<char>>();
			_readOnlyKnownArenaNames = _knownArenaNames.AsReadOnly();

            _arenaConfChanged = ArenaConfChanged;
            _arenaSyncDone = ArenaSyncDone;
            _refreshKnownArenas = RefreshKnownArenas;
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _spawnKey = _playerData.AllocatePlayerData<SpawnLoc>();
            _adKey = ((IArenaManager)this).AllocateArenaData<ArenaData>();

            _mainloopTimer.SetTimer(MainloopTimer_ProcessArenaStates, 100, 100, null);
            _mainloopTimer.SetTimer(MainloopTimer_ReapArenas, 1700, 1700, null);
            _mainloopTimer.SetTimer(MainloopTimer_DoArenaMaintenance, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, null);

            _knownArenaWatcher = new FileSystemWatcher(ArenasDirectoryName);
            _knownArenaWatcher.IncludeSubdirectories = true;
            _knownArenaWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
            _knownArenaWatcher.Created += KnownArenaWatcher_Created;
            _knownArenaWatcher.Deleted += KnownArenaWatcher_Deleted;
            _knownArenaWatcher.Renamed += KnownArenaWatcher_Renamed;
            _knownArenaWatcher.EnableRaisingEvents = true;

            QueueRefreshKnownArenas();

            _iArenaManagerToken = Broker.RegisterInterface<IArenaManager>(this);
            _iArenaManagerInternalToken = Broker.RegisterInterface<IArenaManagerInternal>(this);

            GlobalConfigChangedCallback.Register(Broker, Callback_GlobalConfigChanged);

            return true;
        }

        [ConfigHelp("Arenas", "PermanentArenas", ConfigScope.Global,
            Description = """
                Names of arenas to permanently keep running.
                These arenas will be created when the server is started
                and show up on the arena list, even if no players are in them.
                """)]
        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            _network = broker.GetInterface<INetwork>();
            if (_network is not null)
            {
                _network.AddPacket(C2SPacketType.GotoArena, Packet_GotoArena);
                _network.AddPacket(C2SPacketType.LeaveArena, Packet_LeaveArena);
            }

            _chatNetwork = broker.GetInterface<IChatNetwork>();
            if (_chatNetwork is not null)
            {
                _chatNetwork.AddHandler("GO", ChatHandler_GotoArena);
                _chatNetwork.AddHandler("LEAVE", ChatHandler_LeaveArena);
            }

            _persistExecutor = broker.GetInterface<IPersistExecutor>();

            UpdatePermanentArenas();
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            if (_persistExecutor is not null)
            {
                broker.ReleaseInterface(ref _persistExecutor);
            }

            if (_network is not null)
            {
                _network.RemovePacket(C2SPacketType.GotoArena, Packet_GotoArena);
                _network.RemovePacket(C2SPacketType.LeaveArena, Packet_LeaveArena);
                broker.ReleaseInterface(ref _network);
            }

            if (_chatNetwork is not null)
            {
                _chatNetwork.RemoveHandler("GO", ChatHandler_GotoArena);
                _chatNetwork.RemoveHandler("LEAVE", ChatHandler_LeaveArena);
                broker.ReleaseInterface(ref _chatNetwork);
            }
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (Broker.UnregisterInterface(ref _iArenaManagerToken) != 0)
                return false;

            if (Broker.UnregisterInterface(ref _iArenaManagerInternalToken) != 0)
                return false;

            if (_knownArenaWatcher is not null)
            {
                _knownArenaWatcher.EnableRaisingEvents = false;
                _knownArenaWatcher.Created -= KnownArenaWatcher_Created;
                _knownArenaWatcher.Deleted -= KnownArenaWatcher_Deleted;
                _knownArenaWatcher.Renamed -= KnownArenaWatcher_Renamed;
                _knownArenaWatcher.Dispose();
                _knownArenaWatcher = null;
            }

            _mainloopTimer.ClearTimer(MainloopTimer_ProcessArenaStates, null);
            _mainloopTimer.ClearTimer(MainloopTimer_ReapArenas, null);
            _mainloopTimer.ClearTimer(MainloopTimer_DoArenaMaintenance, null);

            _playerData.FreePlayerData(ref _spawnKey);

            ((IArenaManager)this).FreeArenaData(ref _adKey);

            _arenas.Clear();

            GlobalConfigChangedCallback.Unregister(Broker, Callback_GlobalConfigChanged);

            return true;
        }

        #endregion

        #region IArenaManager

        void IArenaManager.Lock()
        {
            ReadLock();
        }

        void IArenaManager.Unlock()
        {
            ReadUnlock();
        }

        Dictionary<string, Arena>.ValueCollection IArenaManager.Arenas => _arenas.Values;

        bool IArenaManager.RecycleArena(Arena arena)
        {
            WriteLock();
            try
            {
                if (arena.Status != ArenaState.Running)
                    return false;

                _playerData.WriteLock();
                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena &&
                            !player.IsStandard &&
                            !player.IsChat)
                        {
                            _logManager.LogA(LogLevel.Warn, nameof(ArenaManager), arena, "Can't recycle arena with fake players.");
                            return false;
                        }
                    }

                    // first move playing players elsewhere
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena)
                        {
                            // send whoami packet so the clients leave the arena
                            if (player.IsStandard)
                            {
                                S2C_WhoAmI whoAmI = new((short)player.Id);
                                _network?.SendToOne(player, ref whoAmI, NetSendFlags.Reliable);
                            }
                            else if (player.IsChat)
                            {
                                _chatNetwork?.SendToOne(player, $"INARENA:{arena.Name}:{player.Freq}");
                            }

                            // actually initiate the client leaving arena on our side
                            InitiateLeaveArena(player);

                            // and mark the same arena as his desired arena to enter
                            player.NewArena = arena;
                        }
                    }
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                // arena to close and then get resurrected
                arena.Status = ArenaState.Closing;

                if (arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                    arenaData.Resurrect = true;

                return true;
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IArenaManager.SendToArena(Player player, ReadOnlySpan<char> arenaName, int spawnX, int spawnY)
        {
            switch (player.Type)
            {
                case ClientType.Continuum:
                case ClientType.VIE:
                    CompleteGo(
                        player,
                        arenaName,
                        player.Ship,
                        player.Xres,
                        player.Yres,
                        player.Flags.WantAllLvz,
                        player.Packet.AcceptAudio != 0,
                        player.Flags.ObscenityFilter,
                        spawnX,
                        spawnY);
                    break;

                case ClientType.Chat:
                    CompleteGo(
                        player,
                        arenaName,
                        ShipType.Spec,
                        0,
                        0,
                        false,
                        false,
                        player.Flags.ObscenityFilter,
                        0,
                        0);
                    break;
            }
        }

        Arena? IArenaManager.FindArena(ReadOnlySpan<char> name)
        {
            return FindArena(name, ArenaState.Running, ArenaState.Running);
        }

        void IArenaManager.GetPopulationSummary(out int total, out int playing)
        {
            // TODO: Can ArenaManager/Arena be enhanced such that an increment/decrement occurs when players enter/leave, change ships, etc?

            _populationLock.EnterReadLock();
            try
            {
                if (!RefreshNeeded())
                {
                    total = _playersTotal;
                    playing = _playersPlaying;
                    return;
                }
            }
            finally
            {
                _populationLock.ExitReadLock();
            }

            _populationLock.EnterWriteLock();
            try
            {
                if (RefreshNeeded())
                {
                    // Refresh population stats
                    total = playing = 0;

                    ICapabilityManager? capabilityManager = Broker.GetInterface<ICapabilityManager>();

                    try
                    {
                        // This is purposely a read lock, for reading the _arenaDictionary.
                        // Yes, the counts within the Arenas are being changed, but those are synchronized separately inside Arena.SetPlayerCounts(...).
                        // Writing to ArenaData is synchronized with the _populationLock being held.
                        ReadLock();

                        try
                        {
                            // Use the per-arena data object to store temporary counts.
                            foreach (Arena arena in _arenas.Values)
                            {
                                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                                    continue;

                                ad.TotalCount = ad.PlayingCount = 0;
                            }

                            _playerData.Lock();

                            // Go through the players and increment the counts accordingly.
                            try
                            {
                                foreach (Player player in _playerData.Players)
                                {
                                    if (player.Status == PlayerState.Playing
                                        && player.Type != ClientType.Fake
                                        && player.Arena is not null
                                        && (capabilityManager is null || !capabilityManager.HasCapability(player, Constants.Capabilities.ExcludePopulation)))
                                    {
                                        if (!player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
                                            continue;

                                        total++;
                                        ad.TotalCount++;

                                        if (player.Ship != ShipType.Spec)
                                        {
                                            playing++;
                                            ad.PlayingCount++;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }

                            // Update each arena's player counts from the counts in the per-arena data.
                            foreach (Arena arena in _arenas.Values)
                            {
                                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                                    continue;

                                arena.SetPlayerCounts(ad.TotalCount, ad.PlayingCount);
                            }
                        }
                        finally
                        {
                            ReadUnlock();
                        }
                    }
                    finally
                    {
                        if (capabilityManager is not null)
                            Broker.ReleaseInterface(ref capabilityManager);
                    }

                    _playersTotal = total;
                    _playersPlaying = playing;
                    _populationLastRefreshed = DateTime.UtcNow;
                }
                else
                {
                    total = _playersTotal;
                    playing = _playersPlaying;
                }
            }
            finally
            {
                _populationLock.ExitWriteLock();
            }


            // Local helper function
            bool RefreshNeeded() => _populationLastRefreshed == null || (DateTime.UtcNow - _populationLastRefreshed.Value) >= _populationRefreshThreshold;
        }

        ArenaDataKey<T> IArenaManager.AllocateArenaData<T>()
        {
            // Only use of a pool of T objects if there's a way for the objects to be [re]initialized.
            if (typeof(T).IsAssignableTo(typeof(IResettable)))
                return new ArenaDataKey<T>(AllocateArenaData(() => new DefaultPooledExtraDataFactory<T>(_poolProvider)));
            else
                return new ArenaDataKey<T>(AllocateArenaData(() => new NonPooledExtraDataFactory<T>()));
        }

        ArenaDataKey<T> IArenaManager.AllocateArenaData<T>(IPooledObjectPolicy<T> policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            // It's the policy's job to clear/reset an object when it's returned to the pool.
            return new ArenaDataKey<T>(AllocateArenaData(() => new CustomPooledExtraDataFactory<T>(_poolProvider, policy)));
        }

        ArenaDataKey<T> IArenaManager.AllocateArenaData<T>(ObjectPool<T> pool) where T : class
        {
            ArgumentNullException.ThrowIfNull(pool);

            return new ArenaDataKey<T>(AllocateArenaData(() => new CustomPooledExtraDataFactory<T>(pool)));
        }

        bool IArenaManager.FreeArenaData<T>(ref ArenaDataKey<T> key)
        {
            if (key.Id == 0)
            {
                _logManager.LogM(LogLevel.Warn, nameof(ArenaManager), "There was an attempt to FreeArenaData with an uninitialized key (Id = 0).");
                return false;
            }

            WriteLock();

            try
            {
                //
                // Unregister
                //

                if (!_extraDataRegistrations.Remove(key.Id, out ExtraDataFactory? factory))
                    return false;

                //
                // Remove the data from every arena
                //

                foreach (Arena arena in _arenas.Values)
                {
                    if (arena.TryRemoveExtraData(key.Id, out object? data))
                    {
                        factory.Return(data);
                    }
                }

                factory.Dispose();
            }
            finally
            {
                WriteUnlock();
            }

            key = new(0);
            return true;
        }

        void IArenaManager.AddHold(Arena arena)
        {
            WriteLock();
            try
            {
                switch (arena.Status)
                {
                    case ArenaState.WaitHolds0:
                    case ArenaState.WaitHolds1:
                    case ArenaState.WaitHolds2:
                        if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                            return;

                        arenaData.Holds++;
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, $"{nameof(IArenaManager.AddHold)} called from invalid state ({arena.Status}).");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IArenaManager.RemoveHold(Arena arena)
        {
            WriteLock();
            try
            {
                switch (arena.Status)
                {
                    case ArenaState.WaitHolds0:
                    case ArenaState.WaitHolds1:
                    case ArenaState.WaitHolds2:
                        if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                            return;

                        if (arenaData.Holds > 0)
                        {
                            arenaData.Holds--;
                        }
                        else
                        {
                            _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, $"{nameof(IArenaManager.RemoveHold)} called too many times when in state ({arena.Status}). This is indicates a programming bug and needs to be investigated.");
                        }
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, $"{nameof(IArenaManager.RemoveHold)} called from invalid state ({arena.Status}).");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        ReadOnlyTrie IArenaManager.KnownArenaNames => _readOnlyKnownArenaNames;

        #endregion

        #region IArenaManagerInternal

        void IArenaManagerInternal.SendArenaResponse(Player player)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, "Bad arena in SendArenaResponse.");
                return;
            }

            _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "Entering arena.");

            if (player.IsStandard)
            {
                // send whoami packet
                S2C_WhoAmI whoAmI = new((short)player.Id);
                _network?.SendToOne(player, ref whoAmI, NetSendFlags.Reliable);

                // send settings
                IClientSettings? clientSettings = Broker.GetInterface<IClientSettings>();
                if (clientSettings is not null)
                {
                    try
                    {
                        clientSettings.SendClientSettings(player);
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref clientSettings);
                    }
                }
            }
            else if (player.IsChat)
            {
                _chatNetwork?.SendToOne(player, $"INARENA:{arena.Name}:{player.Freq}");
            }

            HashSet<Player> enterPlayerSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Status == PlayerState.Playing
                            && otherPlayer.Arena == arena
                            && otherPlayer != player)
                        {
                            // Add to the collection of players, we'll send later.
                            enterPlayerSet.Add(otherPlayer);

                            // Tell others already in the arena, that the player is entering.
                            SendEnter(player, otherPlayer, false);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (player.IsStandard)
                {
                    enterPlayerSet.Add(player); // include the player's own packet too

                    //
                    // Send all the player entering packets as one large packet.
                    //

                    int packetLength = enterPlayerSet.Count * S2C_PlayerData.Length;
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(packetLength);

                    try
                    {
                        Span<byte> bufferSpan = buffer.AsSpan(0, packetLength); // only the part we are going to use (Rent can return a larger array)

                        int index = 0;
                        S2C_PlayerDataBuilder builder = new(bufferSpan);
                        foreach (Player enteringPlayer in enterPlayerSet)
                        {
                            builder.Set(index++, ref enteringPlayer.Packet);
                        }

                        _network?.SendToOne(player, bufferSpan, NetSendFlags.Reliable);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, true);
                    }
                }
                else if (player.IsChat)
                {
                    foreach (Player enteringPlayer in enterPlayerSet)
                    {
                        SendEnter(enteringPlayer, player, true);
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(enterPlayerSet);
            }

            if (player.IsStandard)
            {
                IMapNewsDownload? mapNewsDownload = Broker.GetInterface<IMapNewsDownload>();
                if (mapNewsDownload is not null)
                {
                    try
                    {
                        mapNewsDownload.SendMapFilename(player);
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref mapNewsDownload);
                    }
                }

                // ASSS sends what it calls a "brick clear" packet here. Which is an empty, 1 byte brick packet (0x21).
                // However, there actually is no such mechanism to clear bricks on the client side. (would be nice to have though)
                // ASSS probably included it to emulate what subgame sends when there are no active bricks.
                // The Bricks module sends brick data on PlayerAction.EnterArena, which happens immediately after this method is called.

                // send entering arena finisher
                Span<byte> span = [(byte)S2CPacketType.EnteringArena];
                _network?.SendToOne(player, span, NetSendFlags.Reliable);

                if (player.TryGetExtraData(_spawnKey, out SpawnLoc? spawnLoc))
                {
                    if ((spawnLoc.X > 0) && (spawnLoc.Y > 0) && (spawnLoc.X < 1024) && (spawnLoc.Y < 1024))
                    {
                        S2C_WarpTo warpTo = new(spawnLoc.X, spawnLoc.Y);
                        _network?.SendToOne(player, ref warpTo, NetSendFlags.Reliable);
                    }
                }
            }

            void SendEnter(Player player, Player playerTo, bool already)
            {
                if (playerTo.IsStandard)
                {
                    _network?.SendToOne(playerTo, ref player.Packet, NetSendFlags.Reliable);
                }
                else if (playerTo.IsChat)
                {
                    _chatNetwork?.SendToOne(playerTo, $"{(already ? "PLAYER" : "ENTERING")}:{player.Name}:{player.Ship:d}:{player.Freq}");
                }
            }
        }

        void IArenaManagerInternal.LeaveArena(Player player)
        {
            LeaveArena(player);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _knownArenaWatcher?.Dispose();
            _knownArenaWatcher = null;
        }

        #endregion

        #region Packet handlers

        [ConfigHelp<bool>("Chat", "ForceFilter", ConfigScope.Global, Default = false,
            Description = "If true, players will always start with the obscenity filter on by default. If false, use their preference.")]
        private void Packet_GotoArena(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != C2S_GoArenaVIE.Length && data.Length != C2S_GoArenaContinuum.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), player, $"Bad arena packet (length={data.Length}).");
                return;
            }

            ref C2S_GoArenaVIE go = ref MemoryMarshal.AsRef<C2S_GoArenaVIE>(data);

            if (go.ShipType > (byte)ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), player, "Bad ship type in arena request.");
                return;
            }

            // make a name from the request
            Span<char> nameBuffer = stackalloc char[Constants.MaxArenaNameLength];
            scoped ReadOnlySpan<char> name;
            int spx = 0;
            int spy = 0;

            if (go.ArenaType == -3) // private arena
            {
                if (!HasCapGo(player))
                    return;

                int charCount = go.ArenaName.Get(nameBuffer);
                name = nameBuffer[..charCount];
            }
            else if (go.ArenaType == -2 || go.ArenaType == -1) // any public arena (server chooses)
            {
                IArenaPlace? arenaPlace = Broker.GetInterface<IArenaPlace>();

                if (arenaPlace is not null)
                {
                    try
                    {
                        if (arenaPlace.TryPlace(player, nameBuffer, out int charsWritten, out spx, out spy))
                        {
                            name = nameBuffer[..charsWritten];
                        }
                        else
                        {
                            name = "0";
                        }
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref arenaPlace);
                    }
                }
                else
                {
                    name = "0";
                }
            }
            else if (go.ArenaType >= 0) // specific public arena
            {
                if (!HasCapGo(player))
                    return;

                go.ArenaType.TryFormat(nameBuffer, out int charsWritten, "d", CultureInfo.InvariantCulture);
                name = nameBuffer[..charsWritten];
            }
            else
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), player, "Bad arena type in arena request.");
                return;
            }

            if (player.Type == ClientType.Continuum)
            {
                // Peer redirects
                IPeer? peer = Broker.GetInterface<IPeer>();
                if (peer is not null)
                {
                    try
                    {
                        if (peer.ArenaRequest(player, go.ArenaType, name))
                            return;
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref peer);
                    }
                }

                // General redirects
                IRedirect? redirect = Broker.GetInterface<IRedirect>();
                if (redirect is not null)
                {
                    try
                    {
                        if (redirect.ArenaRequest(player, name))
                            return;
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref redirect);
                    }
                }
            }

            bool optionalGraphics = false;
            if (data.Length >= C2S_GoArenaContinuum.Length)
            {
                ref C2S_GoArenaContinuum goContinuum = ref MemoryMarshal.AsRef<C2S_GoArenaContinuum>(data);
                optionalGraphics = goContinuum.OptionalGraphics != 0;
            }

            CompleteGo(
                player,
                name,
                (ShipType)go.ShipType,
                go.XRes,
                go.YRes,
                optionalGraphics,
                go.WavMsg != 0,
                (go.ObscenityFilter != 0) || (_configManager.GetBool(_configManager.Global, "Chat", "ForceFilter", ConfigHelp.Constants.Global.Chat.ForceFilter.Default)),
                spx,
                spy);

            bool HasCapGo(Player player)
            {
                if (player is null)
                    return false;

                ICapabilityManager? capabilityManager = Broker.GetInterface<ICapabilityManager>();

                try
                {
                    return capabilityManager is null || capabilityManager.HasCapability(player, "cmd_go");
                }
                finally
                {
                    if (capabilityManager is not null)
                    {
                        Broker.ReleaseInterface(ref capabilityManager);
                    }
                }
            }
        }

        private void Packet_LeaveArena(Player player, Span<byte> data, NetReceiveFlags flags)
        {
#if !CFG_RELAX_LENGTH_CHECKS
            if (data.Length != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), player, $"Bad arena leaving packet (length={data.Length}).");
            }
#endif
            LeaveArena(player);
        }

        #endregion

        #region Chat handlers

        private void ChatHandler_GotoArena(Player player, ReadOnlySpan<char> message)
        {
            bool obscenityFilter = player.Flags.ObscenityFilter || _configManager.GetInt(_configManager.Global, "Chat", "ForceFilter", 0) != 0;
            if (!message.IsEmpty)
            {
                CompleteGo(player, message, ShipType.Spec, 0, 0, false, false, obscenityFilter, 0, 0);
            }
            else
            {
                Span<char> nameBuffer = stackalloc char[Constants.MaxArenaNameLength];

                IArenaPlace? arenaPlace = Broker.GetInterface<IArenaPlace>();
                if (arenaPlace is not null)
                {
                    try
                    {
                        if (arenaPlace.TryPlace(player, nameBuffer, out int charsWritten, out int spawnX, out int spawnY))
                        {
                            nameBuffer = nameBuffer[..charsWritten];
                        }
                        else
                        {
                            nameBuffer[0] = '0';
                            nameBuffer = nameBuffer[..1];
                        }
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref arenaPlace);
                    }
                }
                else
                {
                    nameBuffer[0] = '0';
                    nameBuffer = nameBuffer[..1];
                }

                CompleteGo(player, nameBuffer, ShipType.Spec, 0, 0, false, false, obscenityFilter, 0, 0);
            }
        }

        private void ChatHandler_LeaveArena(Player player, ReadOnlySpan<char> message)
        {
            LeaveArena(player);
        }

        #endregion

        #region Timers

        [ConfigHelp<short>("Team", "SpectatorFrequency", ConfigScope.Arena, Min = 0, Max = 9999, Default = 8025,
            Description = "The frequency that spectators are assigned to, by default.")]
        private bool MainloopTimer_ProcessArenaStates()
        {
            WriteLock();
            try
            {
                foreach (Arena arena in _arenas.Values)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                        continue;

                    ArenaState status = arena.Status;

                    switch (status)
                    {
                        case ArenaState.WaitHolds0:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoInit1;
                            break;

                        case ArenaState.WaitHolds1:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoInit2;
                            break;

                        case ArenaState.WaitHolds2:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoDestroy2;
                            break;
                    }

                    switch (status)
                    {
                        case ArenaState.DoInit0:
                            if (arena.Cfg is null)
                            {
                                // Open the arena's config file.
                                // This operation will most likely do blocking I/O (unless the file was already open/loaded).
                                arenaData.LoadConfigTask ??= _configManager.OpenConfigFileAsync(arena.BaseName, null, _arenaConfChanged, arena);

                                if (!arenaData.LoadConfigTask.IsCompleted)
                                    break;

                                arena.Cfg = arenaData.LoadConfigTask.Result;
                                arenaData.LoadConfigTask = null;

                                if (arena.Cfg is null)
                                {
                                    // TODO: handle the case where a config file couldn't be opened. This is extremely serious. It means that even the default arena config couldn't be opened.
                                    break;
                                }
                            }

                            arena.SpecFreq = short.Clamp((short)_configManager.GetInt(arena.Cfg, "Team", "SpectatorFrequency", Arena.DefaultSpecFreq), TeamSettings.SpectatorFrequency.Min, TeamSettings.SpectatorFrequency.Max);
                            arena.Status = ArenaState.WaitHolds0;
                            Debug.Assert(arenaData.Holds == 0);
                            FireArenaActionCallback(arena, ArenaAction.PreCreate);
                            break;

                        case ArenaState.DoInit1:
                            arenaData.AttachTask ??= DoAttach(arena);
                            if (!arenaData.AttachTask.IsCompleted)
                                break;

                            arenaData.AttachTask.Wait();
                            arenaData.AttachTask = null;

                            arena.Status = ArenaState.WaitHolds1;
                            Debug.Assert(arenaData.Holds == 0);
                            FireArenaActionCallback(arena, ArenaAction.Create);
                            break;

                        case ArenaState.DoInit2:
                            if (_persistExecutor is not null)
                            {
                                arena.Status = ArenaState.WaitSync1;
                                _persistExecutor.GetArena(arena, _arenaSyncDone);
                            }
                            else
                            {
                                arena.Status = ArenaState.Running;
                            }
                            break;

                        case ArenaState.DoWriteData:
                            bool hasPlayers = false;
                            _playerData.Lock();
                            try
                            {
                                foreach (Player p in _playerData.Players)
                                {
                                    if (p.Arena == arena)
                                    {
                                        hasPlayers = true;
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }

                            if (hasPlayers == false)
                            {
                                if (_persistExecutor is not null)
                                {
                                    arena.Status = ArenaState.WaitSync2;
                                    _persistExecutor.PutArena(arena, _arenaSyncDone);
                                }
                                else
                                {
                                    arena.Status = ArenaState.DoDestroy1;
                                }
                            }
                            else
                            {
                                // oops, there is still at least one player still in the arena
                                // let's not destroy this after all
                                arena.Status = ArenaState.Running;
                            }
                            break;

                        case ArenaState.DoDestroy1:
                            arena.Status = ArenaState.WaitHolds2;
                            Debug.Assert(arenaData.Holds == 0);
                            FireArenaActionCallback(arena, ArenaAction.Destroy);
                            break;

                        case ArenaState.DoDestroy2:
                            arenaData.DetachTask ??= _moduleManager.DetachAllFromArenaAsync(arena);
                            if (!arenaData.DetachTask.IsCompleted)
                                break;

                            bool detached = arenaData.DetachTask.Result;
                            arenaData.DetachTask = null;

                            if (detached)
                            {
                                if (arena.Cfg is not null)
                                {
                                    _configManager.CloseConfigFile(arena.Cfg);
                                    arena.Cfg = null;
                                }

                                FireArenaActionCallback(arena, ArenaAction.PostDestroy);

                                if (arenaData.Resurrect)
                                {
                                    // clear all private data on recycle, so it looks to modules like it was just created.
                                    foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                                    {
                                        if (arena.TryRemoveExtraData(keyId, out object? data))
                                        {
                                            factory.Return(data);
                                        }

                                        arena.SetExtraData(keyId, factory.Get());
                                    }

                                    arenaData.Resurrect = false;
                                    arena.Status = ArenaState.DoInit0;
                                }
                                else
                                {
                                    _arenas.Remove(arena.Name);

                                    // remove all the extra data object and return them to their factory
                                    foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                                    {
                                        if (arena.TryRemoveExtraData(keyId, out object? data))
                                        {
                                            factory.Return(data);
                                        }
                                    }

                                    // make sure that any work associated with the arena that is to run on the mainloop is complete
                                    _mainloop.WaitForMainWorkItemDrain();

                                    return true; // kind of hacky, but we can't enumerate again if we modify the dictionary
                                }
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "Failed to detach modules from arena, arena will not be destroyed. Check for correct interface releasing.");

                                _arenas.Remove(arena.Name);
                                string failName = Guid.NewGuid().ToString("N");
                                _arenas.Add(failName, arena);

                                _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "WARNING: The server is no longer in a stable state because of this error. your modules need to be fixed.");

                                // Note: ASSS flushes the log file here.
                                // However, writing of logs is asynchronous, so there's no guarantee the above was written to file before the flush.
                                // Also, file I/O is a blocking operation and should be done on a worker thread.
                                // Instead, I decided to skip it and just going to let it flush itself (happens periodically).

                                arenaData.Resurrect = false;
                                arenaData.Reap = false;
                                arena.KeepAlive = true;
                                arena.Status = ArenaState.Running;
                            }
                            break;
                    }
                }
            }
            finally
            {
                WriteUnlock();
            }

            return true;


            [ConfigHelp("Modules", "AttachModules", ConfigScope.Arena,
            Description = """
                This is a list of modules that you want to take effect in this arena. 
                Not all modules need to be attached to arenas to function, but some do.
                """)]
            async Task DoAttach(Arena arena)
            {
                string? attachMods = _configManager.GetStr(arena.Cfg!, "Modules", "AttachModules");
                if (string.IsNullOrWhiteSpace(attachMods))
                    return;

                string[] attachModsArray = attachMods.Split(" \t:;".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

                foreach (string moduleToAttach in attachModsArray)
                {
                    await _moduleManager.AttachModuleAsync(moduleToAttach, arena);
                }
            }
        }

        private void ArenaConfChanged(Arena arena)
        {
            if (arena is null)
                return;

            ReadLock();
            try
            {
                // only running arenas should receive confchanged events
                if (arena.Status == ArenaState.Running)
                {
                    FireArenaActionCallback(arena, ArenaAction.ConfChanged);
                }
            }
            finally
            {
                ReadUnlock();
            }
        }

        /// <summary>
        /// This is called when the persistent data retrieval or saving has completed.
        /// </summary>
        /// <param name="arena"></param>
        private void ArenaSyncDone(Arena arena)
        {
            WriteLock();

            try
            {
                if (arena.Status == ArenaState.WaitSync1)
                {
                    // persistent data has been retrieved from the database
                    arena.Status = ArenaState.Running;
                }
                else if (arena.Status == ArenaState.WaitSync2)
                {
                    // persistent data has been saved to the database
                    arena.Status = ArenaState.DoDestroy1;
                }
                else
                {
                    _logManager.LogA(LogLevel.Warn, nameof(ArenaManager), arena, $"ArenaSyncDone called from the wrong state ({arena.Status}).");
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        private static void FireArenaActionCallback(Arena arena, ArenaAction action)
        {
            ArenaActionCallback.Fire(arena, arena, action);
        }

        private bool MainloopTimer_ReapArenas()
        {
            WriteLock();

            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Arena arena in _arenas.Values)
                    {
                        if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                            continue;

                        arenaData.Reap = arena.Status == ArenaState.Running || arena.Status == ArenaState.Closing;
                    }

                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena is not null
                            && player.Arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                        {
                            arenaData.Reap = false;
                        }

                        if (player.NewArena is not null
                            && player.Arena != player.NewArena
                            && player.NewArena.TryGetExtraData(_adKey, out arenaData))
                        {
                            if (player.NewArena.Status == ArenaState.Closing)
                            {
                                arenaData.Resurrect = true;
                            }
                            else
                            {
                                arenaData.Reap = false;
                            }
                        }
                    }

                    foreach (Arena arena in _arenas.Values)
                    {
                        if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                            continue;

                        if (arenaData.Reap && (arena.Status == ArenaState.Closing || !arena.KeepAlive))
                        {
                            _logManager.LogA(LogLevel.Drivel, nameof(ArenaManager), arena,
                                $"Arena being {((arena.Status == ArenaState.Running) ? "destroyed" : "recycled")}.");

                            // set its status so that the arena processor will do appropriate things
                            arena.Status = ArenaState.DoWriteData;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
            finally
            {
                WriteUnlock();
            }

            return true;
        }

        private bool MainloopTimer_DoArenaMaintenance()
        {
            WriteLock();

            try
            {
                foreach (Arena arena in _arenas.Values)
                {
                    arena.CleanupTeamTargets();
                }
            }
            finally
            {
                WriteUnlock();
            }

            return true;
        }

        private void QueueRefreshKnownArenas()
        {
            lock (_refreshLock)
            {
                _isRefreshKnownArenasRequested = true;

                // Queue up a task if there isn't already one.
                _refreshKnownArenasTask ??= Task.Run(_refreshKnownArenas);
            }
        }

        private void RefreshKnownArenas()
        {
            while (true)
            {
                lock (_refreshLock)
                {
                    if (!_isRefreshKnownArenasRequested)
                    {
                        _refreshKnownArenasTask = null;
                        break;
                    }

                    _isRefreshKnownArenasRequested = false;
                }

                _logManager.LogM(LogLevel.Info, nameof(ArenaManager), "Refreshing known arenas.");

                HashSet<string> arenaNames = _objectPoolManager.NameHashSetPool.Get();
                try
                {
                    try
                    {
                        foreach (string dirPath in Directory.GetDirectories(ArenasDirectoryName))
                        {
                            ReadOnlySpan<char> arenaName = dirPath.AsSpan()[(ArenasDirectoryName.Length + 1)..];
                            if (arenaName.Equals("(default)", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (arenaName.StartsWith(".", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!File.Exists(Path.Join(dirPath, "arena.conf")))
                                continue;

                            arenaNames.Add(arenaName.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ArenaManager), $"Error refreshing known arenas. {ex.Message}");
                    }

                    WriteLock();
                    try
                    {
                        _knownArenaNames.Clear();

                        foreach (string arenaName in arenaNames)
                        {
                            _knownArenaNames.Add(arenaName);
                        }
                    }
                    finally
                    {
                        WriteUnlock();
                    }
                }
                finally
                {
                    _objectPoolManager.NameHashSetPool.Return(arenaNames);
                }
            }
        }

        #endregion

        #region Locks

        private void ReadLock()
        {
            _arenaLock.EnterReadLock();
        }

        private void ReadUnlock()
        {
            _arenaLock.ExitReadLock();
        }

        private void WriteLock()
        {
            _arenaLock.EnterWriteLock();
        }

        private void WriteUnlock()
        {
            _arenaLock.ExitWriteLock();
        }

        #endregion

        private void CompleteGo(Player player, ReadOnlySpan<char> requestName, ShipType ship, int xRes, int yRes, bool gfx, bool voices, bool obscene, int spawnX, int spawnY)
        {
            // status should be LoggedIn or Playing at this point
            if (player.Status != PlayerState.LoggedIn && player.Status != PlayerState.Playing && player.Status != PlayerState.LeavingArena)
            {
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, $"State sync problem: Sent arena request from bad status ({player.Status}).");
                return;
            }

            // remove all illegal characters and make lowercase
            Span<char> name = stackalloc char[Constants.MaxArenaNameLength];
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append(requestName);
                for (int x = 0; x < sb.Length; x++)
                {
                    if (x == 0 && sb[x] == '#') // Initial pound sign is allowed (indicates a private arena).
                        continue;
                    else if (!char.IsAsciiLetterOrDigit(sb[x])) // A-Z, a-z, and 0-9 only. Purposely, no extended ASCII characters.
                        sb[x] = 'x';
                    else if (char.IsAsciiLetterUpper(sb[x]))
                        sb[x] = char.ToLower(sb[x]);
                }

                int charsWritten;
                if (sb.Length == 0)
                {
                    // this might occur when a player is redirected to us from another zone
                    IArenaPlace? arenaPlace = Broker.GetInterface<IArenaPlace>();
                    if (arenaPlace is not null)
                    {
                        try
                        {
                            if (arenaPlace.TryPlace(player, name, out charsWritten, out _, out _))
                            {
                                name = name[..charsWritten];
                            }
                            else
                            {
                                "0".CopyTo(name);
                                name = name[..1];
                            }
                        }
                        finally
                        {
                            Broker.ReleaseInterface(ref arenaPlace);
                        }
                    }
                    else
                    {
                        "0".CopyTo(name);
                        name = name[..1];
                    }
                }
                else
                {
                    charsWritten = Math.Min(sb.Length, name.Length);
                    sb.CopyTo(0, name, charsWritten);
                    name = name[..charsWritten];
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            if (player.Arena is not null)
                LeaveArena(player);

            WriteLock();
            try
            {
                // Try to locate an existing arena.
                Arena? arena = FindArena(name, ArenaState.DoInit0, ArenaState.DoDestroy2);

                if (arena is null)
                {
                    // Create a non-permanent arena.
                    arena = CreateArena(name, false);
                }
                else if (arena.Status > ArenaState.Running)
                {
                    // The arena is on it's way out.
                    // This isn't a problem, just make sure that it will come back.
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                        return;

                    arenaData.Resurrect = true;
                }

                // Set up player info.
                _playerData.WriteLock();
                try
                {
                    player.NewArena = arena;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                player.Ship = ship;
                player.Xres = (short)xRes;
                player.Yres = (short)yRes;
                player.Flags.WantAllLvz = gfx;
                player.Packet.AcceptAudio = voices ? (byte)1 : (byte)0;
                player.Flags.ObscenityFilter = obscene;

                if (player.TryGetExtraData(_spawnKey, out SpawnLoc? spawnLoc))
                {
                    spawnLoc.X = (short)spawnX;
                    spawnLoc.Y = (short)spawnY;
                }
            }
            finally
            {
                WriteUnlock();
            }

            // Don't mess with player status yet, let him stay in PlayerState.LoggedIn. 
            // It will be transitioned when the arena is ready.
        }


        private Arena? FindArena(ReadOnlySpan<char> name, ArenaState? minState, ArenaState? maxState)
        {
            ReadLock();
            try
            {
                if (!_arenasLookup.TryGetValue(name, out Arena? arena))
                    return null;

                if (minState is not null && arena.Status < minState)
                    return null;

                if (maxState is not null && arena.Status > maxState)
                    return null;

                return arena;
            }
            finally
            {
                ReadUnlock();
            }
        }

        private Arena CreateArena(ReadOnlySpan<char> name, bool permanent)
        {
            string arenaName = StringPool.Shared.GetOrAdd(name);
            Arena arena = new(Broker, arenaName, this);
            arena.KeepAlive = permanent;

            WriteLock();

            try
            {
                foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                {
                    arena.SetExtraData(keyId, factory.Get());
                }

                _arenas.Add(arenaName, arena);
            }
            finally
            {
                WriteUnlock();
            }

            _logManager.LogA(LogLevel.Info, nameof(ArenaManager), arena, "Created arena.");

            return arena;
        }

        private Arena RemoveArena(ReadOnlySpan<char> name)
        {
            string arenaName = StringPool.Shared.GetOrAdd(name);
            Arena arena = new(Broker, arenaName, this);

            WriteLock();

            try
            {
                _arenas.Remove(arenaName);
            }
            finally
            {
                WriteUnlock();
            }

            _logManager.LogA(LogLevel.Info, nameof(ArenaManager), arena, "Removed arena.");

            return arena;
        }

        private void LeaveArena(Player player)
        {
            bool notify;
            Arena? arena;

            _playerData.WriteLock();
            try
            {
                arena = player.Arena;
                if (arena is null)
                    return;

                notify = InitiateLeaveArena(player);
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (notify)
            {
                S2C_PlayerLeaving packet = new((short)player.Id);
                _network?.SendToArena(arena, player, ref packet, NetSendFlags.Reliable);
                _chatNetwork?.SendToArena(arena, player, $"LEAVING:{player.Name}");

                _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "Leaving arena.");
            }
        }

        private bool InitiateLeaveArena(Player player)
        {
            bool notify = false;

            // This messy logic attempts to deal with players who haven't fully
            // entered an arena yet. It will try to insert them at the proper
            // stage of the arena leaving process so things that have been done
            // get undone, and things that haven't been done _don't_ get undone.
            switch (player.Status)
            {
                case PlayerState.LoggedIn:
                case PlayerState.DoFreqAndArenaSync:
                    // For these 2, nothing much has been done. Just go back to PlayerState.LoggedIn.
                    player.Status = PlayerState.LoggedIn;
                    break;

                case PlayerState.WaitArenaSync1:
                    // This is slightly tricky: we want to wait until persist is
                    // done loading the scores before changing the state, or
                    // things will get screwed up. So, mark it here and let core
                    // take care of it. This is really messy and it would be
                    // nice to find a better way to handle it. 
                    player.Flags.LeaveArenaWhenDoneWaiting = true;
                    break;

                case PlayerState.ArenaRespAndCBS:
                    // In these, stuff has come out of the database. Put it back in.
                    player.Status = PlayerState.DoArenaSync2;
                    break;

                case PlayerState.Playing:
                    // Do all of the above, plus call leaving callbacks.
                    player.Status = PlayerState.LeavingArena;
                    notify = true;
                    break;

                case PlayerState.LeavingArena:
                case PlayerState.DoArenaSync2:
                case PlayerState.WaitArenaSync2:
                case PlayerState.LeavingZone:
                case PlayerState.WaitGlobalSync2:
                    // No problem, player is already on the way out.
                    break;

                default:
                    // Something's wrong here.
                    _logManager.LogP(LogLevel.Error, nameof(ArenaManager), player, $"Player has an arena, but is in a bad state ({player.Status}).");
                    notify = true;
                    break;
            }

            return notify;
        }

        private int AllocateArenaData(Func<ExtraDataFactory> createExtraDataFactoryFunc)
        {
            ArgumentNullException.ThrowIfNull(createExtraDataFactoryFunc);

            WriteLock();

            try
            {
                //
                // Register
                //

                int keyId;

                // find next available
                for (keyId = 1; keyId <= _extraDataRegistrations.Keys.Count; keyId++)
                {
                    if (_extraDataRegistrations.Keys[keyId - 1] != keyId)
                        break;
                }

                ExtraDataFactory factory = createExtraDataFactoryFunc();
                _extraDataRegistrations[keyId] = factory;

                //
                // Add the data to each arena.
                //

                foreach (Arena arena in _arenas.Values)
                {
                    arena.SetExtraData(keyId, factory.Get());
                }

                return keyId;
            }
            finally
            {
                WriteUnlock();
            }
        }

        private void KnownArenaWatcher_Created(object sender, FileSystemEventArgs e)
        {
            // Directory or arena.conf created.
            if (MemoryExtensions.Equals(Path.GetDirectoryName(e.FullPath.AsSpan()), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)
                || (MemoryExtensions.Equals(Path.GetFileName(e.FullPath.AsSpan()), "arena.conf", StringComparison.OrdinalIgnoreCase)
                    && MemoryExtensions.Equals(Path.GetDirectoryName(Path.GetDirectoryName(e.FullPath.AsSpan())), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)))
            {
                QueueRefreshKnownArenas();
            }
        }

        private void KnownArenaWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            // Directory or arena.conf deleted.
            if (MemoryExtensions.Equals(Path.GetDirectoryName(e.FullPath.AsSpan()), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)
                || (MemoryExtensions.Equals(Path.GetFileName(e.FullPath.AsSpan()), "arena.conf", StringComparison.OrdinalIgnoreCase)
                    && MemoryExtensions.Equals(Path.GetDirectoryName(Path.GetDirectoryName(e.FullPath.AsSpan())), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)))
            {
                QueueRefreshKnownArenas();
            }
        }

        private void KnownArenaWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            // Directory or arena.conf renamed.
            if (MemoryExtensions.Equals(Path.GetDirectoryName(e.FullPath.AsSpan()), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)
                || (MemoryExtensions.Equals(Path.GetFileName(e.FullPath.AsSpan()), "arena.conf", StringComparison.OrdinalIgnoreCase)
                    && MemoryExtensions.Equals(Path.GetDirectoryName(Path.GetDirectoryName(e.FullPath.AsSpan())), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase))
                || (MemoryExtensions.Equals(Path.GetFileName(e.OldFullPath.AsSpan()), "arena.conf", StringComparison.OrdinalIgnoreCase)
                    && MemoryExtensions.Equals(Path.GetDirectoryName(Path.GetDirectoryName(e.OldFullPath.AsSpan())), ArenasDirectoryName, StringComparison.OrdinalIgnoreCase)))
            {
                QueueRefreshKnownArenas();
            }
        }

        private void UpdatePermanentArenas()
        {
            string? permanentArenas = _configManager.GetStr(_configManager.Global, "Arenas", "PermanentArenas");
            if (!string.IsNullOrWhiteSpace(permanentArenas))
            {
                int totalCreated = 0;
                int totalRemoved = 0;

                ReadOnlySpan<char> remaining = permanentArenas;
                ReadOnlySpan<char> arenaName;
                          
                //Create new PermanentArenas that don't exist yet
                while (!(arenaName = remaining.GetToken(", \t\n", out remaining)).IsEmpty)
                {
                    if (!_arenas.ContainsKey(arenaName.ToString())) //TODO:  It's possible an arena could exist but not be permanent so wouldn't be converted, would need to also check if arena.KeepAlive
                    {
                        ++totalCreated;
                        _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Creating permanent arena '{arenaName}'.");
                        CreateArena(arenaName, true);
                    }
                }

                //Cycle through existing PermanenetArenas and remove those not configured
                foreach (Arena arena in _arenas.Values) {
                    // Ignore public # arenas, might not be necessary
                    if (!Regex.IsMatch(arena.Name, "\\d")) { 
                        if (!permanentArenas.Split(new char[0]).Contains(arena.Name) && arena.KeepAlive == true)
                        {
                            ++totalRemoved;
                            _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Removing permanent arena '{arena.Name}'.");
                            RemoveArena(arena.Name);
                        }
                    }
                }

                if (totalCreated > 0)
                {
                    _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Created {totalCreated} permanent arena(s).");
                }

                if (totalRemoved > 0) {
                    _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Removed {totalRemoved} permanent arena(s).");
                }
            }
        }

        private void Callback_GlobalConfigChanged()
        {
            UpdatePermanentArenas();
        }

        #region Helper types

        private class SpawnLoc(short x, short y) : IResettable
        {
            public short X = x;
            public short Y = y;

            public SpawnLoc() : this(0, 0)
            {
            }

            bool IResettable.TryReset()
            {
                X = 0;
                Y = 0;
                return true;
            }
        }

        private class ArenaData : IResettable
        {
            /// <summary>
            /// counter for the # of holds on the arena
            /// </summary>
            public int Holds = 0;

            /// <summary>
            /// whether the arena should be recreated after it is destroyed
            /// </summary>
            public bool Resurrect = false;

            public bool Reap = false;

            public int TotalCount = 0;
            public int PlayingCount = 0;

            public Task<ConfigHandle?>? LoadConfigTask = null;
            public Task? AttachTask = null;
            public Task<bool>? DetachTask = null;
            bool IResettable.TryReset()
            {
                Holds = 0;
                Resurrect = false;
                Reap = false;
                TotalCount = 0;
                PlayingCount = 0;
                LoadConfigTask = null;
                AttachTask = null;
                DetachTask = null;
                return true;
            }
        }

        #endregion
    }
}
