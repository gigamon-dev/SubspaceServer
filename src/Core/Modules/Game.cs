using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArenaSettings = SS.Core.ConfigHelp.Constants.Arena;
using SSProto = SS.Core.Persist.Protobuf;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages the core game state.
    /// </summary>
    [CoreModuleInfo]
    public sealed class Game(
        IComponentBroker broker,
        IArenaManager arenaManager,
        IChat chat,
        IClientSettings clientSettings,
        IConfigManager configManager,
        ILagCollect lagCollect,
        ILogManager logManager,
        IMainloop mainloop,
        IMapData mapData,
        INetwork network,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData,
        IPrng prng) : IAsyncModule, IGame
    {
        // Required dependencies
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly IClientSettings _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILagCollect _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IMapData _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private readonly IPrng _prng = prng ?? throw new ArgumentNullException(nameof(prng));

        // Optional dependencies
        private ICapabilityManager? _capabilityManager;
        private IChatNetwork? _chatNetwork;
        private ICommandManager? _commandManager;
        private IPersist? _persist;

        private InterfaceRegistrationToken<IGame>? _iGameToken;

        private PlayerDataKey<PlayerData> _pdKey;
        private ArenaDataKey<ArenaData> _adKey;

        private readonly ClientSettingIdentifier[] _shipBombFireDelayIds = new ClientSettingIdentifier[8];
        private ClientSettingIdentifier _flaggerBombFireDelayId;
        private ClientSettingIdentifier _soccerUseFlaggerId;

        private DelegatePersistentData<Player>? _persistRegistration;

        private readonly Lock _specLock = new();
        private readonly Lock _freqShipLock = new();

        #region IModule Members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _capabilityManager = broker.GetInterface<ICapabilityManager>();
            _chatNetwork = broker.GetInterface<IChatNetwork>();
            _commandManager = broker.GetInterface<ICommandManager>();
            _persist = broker.GetInterface<IPersist>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            string[] shipNames = System.Enum.GetNames<ShipType>();
            for (int i = 0; i < 8; i++)
            {
                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[i], "BombFireDelay", out ClientSettingIdentifier id))
                {
                    _logManager.LogM(LogLevel.Error, nameof(Game), $"Error getting ClientSettingIdentifier {shipNames[i]}:BombFireDelay");
                    return false;
                }

                _shipBombFireDelayIds[i] = id;
            }

            if (!_clientSettings.TryGetSettingsIdentifier("Flag", "FlaggerBombFireDelay", out _flaggerBombFireDelayId))
            {
                _logManager.LogM(LogLevel.Error, nameof(Game), "Error getting ClientSettingIdentifier Flag:FlaggerBombFireDelay.");
                return false;
            }

            if (!_clientSettings.TryGetSettingsIdentifier("Soccer", "UseFlagger", out _soccerUseFlaggerId))
            {
                _logManager.LogM(LogLevel.Error, nameof(Game), "Error getting ClientSettingIdentifier Soccer:UseFlagger.");
                return false;
            }

            if (_persist is not null)
            {
                _persistRegistration = new(
                    (int)PersistKey.GameShipLock, PersistInterval.ForeverNotShared, PersistScope.PerArena, Persist_GetShipLockData, Persist_SetShipLockData, null);

                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            NewPlayerCallback.Register(_broker, Callback_NewPlayer);
            FlagGameResetCallback.Register(_broker, Callback_FlagGameReset);

            _network.AddPacket(C2SPacketType.Position, Packet_Position);
            _network.AddPacket(C2SPacketType.SpecRequest, Packet_SpecRequest);
            _network.AddPacket(C2SPacketType.SetShip, Packet_SetShip);
            _network.AddPacket(C2SPacketType.SetFreq, Packet_SetFreq);
            _network.AddPacket(C2SPacketType.Die, Packet_Die);
            _network.AddPacket(C2SPacketType.Green, Packet_Green);
            _network.AddPacket(C2SPacketType.AttachTo, Packet_AttachTo);
            _network.AddPacket(C2SPacketType.TurretKickOff, Packet_TurretKickoff);

            _chatNetwork?.AddHandler("CHANGEFREQ", ChatHandler_ChangeFreq);

            if (_commandManager is not null)
            {
                _commandManager.AddCommand("spec", Command_spec);
                _commandManager.AddCommand("energy", Command_energy);
            }

            _iGameToken = _broker.RegisterInterface<IGame>(this);

            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iGameToken) != 0)
                return false;

            if (_commandManager is not null)
            {
                _commandManager.RemoveCommand("spec", Command_spec, null);
                _commandManager.RemoveCommand("energy", Command_energy, null);
            }

            _chatNetwork?.RemoveHandler("CHANGEFREQ", ChatHandler_ChangeFreq);

            _network.RemovePacket(C2SPacketType.Position, Packet_Position);
            _network.RemovePacket(C2SPacketType.SpecRequest, Packet_SpecRequest);
            _network.RemovePacket(C2SPacketType.SetShip, Packet_SetShip);
            _network.RemovePacket(C2SPacketType.SetFreq, Packet_SetFreq);
            _network.RemovePacket(C2SPacketType.Die, Packet_Die);
            _network.RemovePacket(C2SPacketType.Green, Packet_Green);
            _network.RemovePacket(C2SPacketType.AttachTo, Packet_AttachTo);
            _network.RemovePacket(C2SPacketType.TurretKickOff, Packet_TurretKickoff);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(_broker, Callback_NewPlayer);
            FlagGameResetCallback.Unregister(_broker, Callback_FlagGameReset);

            _mainloop.WaitForMainWorkItemDrain();

            if (_capabilityManager is not null)
            {
                broker.ReleaseInterface(ref _capabilityManager);
            }

            if (_chatNetwork is not null)
            {
                broker.ReleaseInterface(ref _chatNetwork);
            }

            if (_commandManager is not null)
            {
                broker.ReleaseInterface(ref _commandManager);
            }

            if (_persist is not null)
            {
                if (_persistRegistration is not null)
                {
                    await _persist.UnregisterPersistentDataAsync(_persistRegistration);
                }

                broker.ReleaseInterface(ref _persist);
            }

            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IGame Members

        void IGame.SetFreq(Player player, short freq)
        {
            if (player is null)
                return;

            SetFreq(player, freq);
        }

        void IGame.SetShip(Player player, ShipType ship)
        {
            if (player is null)
                return;

            SetShipAndFreq(player, ship, player.Freq);
        }

        void IGame.SetShipAndFreq(Player player, ShipType ship, short freq)
        {
            if (player is null)
                return;

            SetShipAndFreq(player, ship, freq);
        }

        void IGame.WarpTo(ITarget target, short x, short y)
        {
            if (target is null)
                return;

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.TargetToSet(target, players, static player => (player.ClientFeatures & ClientFeatures.WarpTo) != 0);

                S2C_WarpTo warpTo = new(x, y);
                _network.SendToSet(players, ref warpTo, NetSendFlags.Reliable | NetSendFlags.Urgent);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        void IGame.GivePrize(ITarget target, Prize prize, short count)
        {
            if (target is null)
                return;

            S2C_PrizeReceive packet = new(count, prize);
            _network.SendToTarget(target, ref packet, NetSendFlags.Reliable);
        }

        void IGame.Lock(ITarget target, bool notify, bool spec, int timeout)
        {
            if (target is null)
                return;

            LockWork(target, true, notify, spec, timeout);
        }

        void IGame.Unlock(ITarget target, bool notify)
        {
            if (target is null)
                return;

            LockWork(target, false, notify, false, 0);
        }

        bool IGame.HasLock(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return false;

            return pd.LockShip;
        }

        void IGame.LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.InitLockShip = true;
            if (!initial)
                ad.InitSpec = true;

            if (!onlyArenaState)
            {
                LockWork(arena, true, notify, spec, 0);
            }
        }

        void IGame.UnlockArena(Arena arena, bool notify, bool onlyArenaState)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.InitLockShip = false;
            ad.InitSpec = false;

            if (!onlyArenaState)
            {
                LockWork(arena, false, notify, false, 0);
            }
        }

        bool IGame.HasLock(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            return ad.InitLockShip;
        }

        void IGame.FakePosition(Player player, ref C2S_PositionPacket pos)
        {
            if (player is null)
                return;

            ExtraPositionData dummyExtra = new();
            HandlePositionPacket(player, ref pos, ref dummyExtra, false, true);
        }

        void IGame.FakePosition(Player player, ref C2S_PositionPacket pos, ref ExtraPositionData extra)
        {
            if (player is null)
                return;

            HandlePositionPacket(player, ref pos, ref extra, true, true);
        }

        void IGame.FakeKill(Player killer, Player killed, short pts, short flags)
        {
            if (killer is null || killed is null)
                return;

            NotifyKill(killer, killed, pts, flags, 0);
        }

        double IGame.GetIgnoreWeapons(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return 0;

            return pd.IgnoreWeapons / (double)Constants.RandMax;
        }

        void IGame.SetIgnoreWeapons(Player player, double proportion)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            pd.IgnoreWeapons = (int)(Constants.RandMax * proportion);
        }

        void IGame.ShipReset(ITarget target)
        {
            if (target is null)
                return;

            ReadOnlySpan<byte> shipResetBytes = [(byte)S2CPacketType.ShipReset];
            _network.SendToTarget(target, shipResetBytes, NetSendFlags.Reliable);

            _playerData.Lock();

            try
            {
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    _playerData.TargetToSet(target, players);

                    foreach (Player player in players)
                    {
                        if (player.Ship == ShipType.Spec)
                            continue;

                        SpawnCallback.SpawnReason flags = SpawnCallback.SpawnReason.ShipReset;
                        if (player.Flags.IsDead)
                        {
                            player.Flags.IsDead = false;
                            flags |= SpawnCallback.SpawnReason.AfterDeath;
                        }

                        DoSpawnCallback(player, flags);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        void IGame.SetPlayerEnergyViewing(Player player, SeeEnergy value)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            pd.SeeNrg = value;
        }

        void IGame.SetSpectatorEnergyViewing(Player player, SeeEnergy value)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            pd.SeeNrgSpec = value;
        }

        void IGame.ResetPlayerEnergyViewing(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            SeeEnergy seeNrg = SeeEnergy.None;
            if (ad.AllSeeEnergy != SeeEnergy.None)
                seeNrg = ad.AllSeeEnergy;

            if (_capabilityManager is not null
                && _capabilityManager.HasCapability(player, Constants.Capabilities.SeeEnergy))
            {
                seeNrg = SeeEnergy.All;
            }

            pd.SeeNrg = seeNrg;
        }

        void IGame.ResetSpectatorEnergyViewing(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            SeeEnergy seeNrgSpec = SeeEnergy.None;
            if (ad.SpecSeeEnergy != SeeEnergy.None)
                seeNrgSpec = ad.SpecSeeEnergy;

            if (_capabilityManager is not null
                && _capabilityManager.HasCapability(player, Constants.Capabilities.SeeEnergy))
            {
                seeNrgSpec = SeeEnergy.All;
            }

            pd.SeeNrgSpec = seeNrgSpec;
        }

        void IGame.AddExtraPositionDataWatch(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.EpdWatchCount == 0)
            {
                SendSpecBytes(player, true);
            }

            playerData.EpdModuleWatchCount++;
        }

        void IGame.RemoveExtraPositionDataWatch(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            playerData.EpdModuleWatchCount--;

            if (playerData.EpdWatchCount == 0)
            {
                SendSpecBytes(player, false);
            }
        }

        bool IGame.IsAntiwarped(Player player, HashSet<Player>? playersAntiwarping)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return false;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (playerData.MapRegionNoAnti)
                return false;

            bool antiwarped = false;

            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPlayerData))
                        continue;

                    if (otherPlayer.Arena == player.Arena
                        && otherPlayer.Freq != player.Freq
                        && otherPlayer.Ship != ShipType.Spec
                        && (otherPlayer.Position.Status & PlayerPositionStatus.Antiwarp) != 0
                        && !otherPlayerData.MapRegionNoAnti
                        && ((otherPlayer.Position.Status & PlayerPositionStatus.Safezone) != 0 || !ad.NoSafeAntiWarp))
                    {
                        int dx = otherPlayer.Position.X - player.Position.X;
                        int dy = otherPlayer.Position.Y - player.Position.Y;
                        int distSquared = dx * dx + dy * dy;

                        if (distSquared < ad.AntiWarpRange)
                        {
                            antiwarped = true;

                            if (playersAntiwarping is not null)
                            {
                                playersAntiwarping.Add(otherPlayer);
                            }
                            else
                            {
                                // we found one, but no list to populate, so we're done
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return antiwarped;
        }

        void IGame.Attach(Player player, Player? to)
        {
            if (player is null)
                return;

            if (player.Status != PlayerState.Playing
                || player.Arena is null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Game), $"Failed to force attach player {player.Id} as a turret.");
                return;
            }

            if (to is not null)
            {
                if (to == player
                    || to.Status != PlayerState.Playing
                    || to.Arena != player.Arena
                    || to.Freq != player.Freq)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Game), $"Failed to force attach player {player.Id} as a turret onto player {to.Id}.");
                    return;
                }
            }

            Attach(player, to);
        }

        void IGame.TurretKickoff(Player player)
        {
            if (player is null)
                return;

            if (player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            S2C_TurretKickoff packet = new((short)player.Id);
            _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable);

            TurretKickoffCallback.Fire(arena, player);
        }

        void IGame.GetSpectators(Player target, HashSet<Player> spectators)
        {
            if (target is null || spectators is null)
                return;

            if (target.Ship == ShipType.Spec)
                return;

            lock (_specLock)
            {
                _playerData.Lock();
                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPlayerData))
                            continue;

                        if (otherPlayerData.Speccing == target)
                        {
                            spectators.Add(otherPlayer);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        void IGame.GetSpectators(HashSet<Player> targets, HashSet<Player> spectators)
        {
            if (targets is null || spectators is null)
                return;

            lock (_specLock)
            {
                _playerData.Lock();
                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPlayerData))
                            continue;

                        Player? target = otherPlayerData.Speccing;
                        if (target is null)
                            continue;

                        if (target.Ship == ShipType.Spec)
                            continue;

                        if (targets.Contains(target))
                        {
                            spectators.Add(otherPlayer);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        #endregion

        #region Persist methods

        private void Persist_GetShipLockData(Player? player, Stream outStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (_freqShipLock)
            {
                ExpireLock(player);

                if (pd.Expires is not null)
                {
                    SSProto.ShipLock protoShipLock = new();
                    protoShipLock.Expires = Timestamp.FromDateTime(pd.Expires.Value);

                    protoShipLock.WriteTo(outStream);
                }
            }
        }

        private void Persist_SetShipLockData(Player? player, Stream inStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (_freqShipLock)
            {
                SSProto.ShipLock protoShipLock = SSProto.ShipLock.Parser.ParseFrom(inStream);
                pd.Expires = protoShipLock.Expires.ToDateTime();
                pd.LockShip = true;

                // Try expiring once now, and...
                ExpireLock(player);

                // If the lock is still active, force to spec.
                if (pd.LockShip)
                {
                    player.Ship = ShipType.Spec;
                    player.Freq = player.Arena!.SpecFreq;
                }
            }
        }

        #endregion

        // Flag:FlaggerKillMultiplier is a client setting, so its [ConfigHelp] in ClientSettingsConfig.cs
        [ConfigHelp<int>("Misc", "RegionCheckInterval", ConfigScope.Arena, Default = 100,
            Description = "How often to check for region enter/exit events (in ticks).")]
        [ConfigHelp<bool>("Misc", "SpecSeeExtra", ConfigScope.Arena, Default = true,
            Description = "Whether spectators can see extra data for the person they're spectating.")]
        [ConfigHelp<SeeEnergy>("Misc", "SpecSeeEnergy", ConfigScope.Arena, Default = SeeEnergy.All,
            Description = "Whose energy levels spectators can see. The options are the same as for Misc:SeeEnergy, with one addition: 'Spec' means only the player you're spectating.")]
        [ConfigHelp<SeeEnergy>("Misc", "SeeEnergy", ConfigScope.Arena, Default = SeeEnergy.None,
            Description = "Whose energy levels everyone can see: 'None' means nobody else's, 'All' is everyone's, 'Team' is only teammates.")]
        [ConfigHelp<int>("Security", "MaxDeathWithoutFiring", ConfigScope.Arena, Default = 5,
            Description = "The number of times a player can die without firing a weapon before being placed in spectator mode.")]
        [ConfigHelp<bool>("Misc", "NoSafeAntiwarp", ConfigScope.Arena, Default = false,
            Description = "Disables antiwarp on players in safe zones.")]
        [ConfigHelp<int>("Misc", "WarpThresholdDelta", ConfigScope.Arena, Default = 320,
            Description = "The amount of change in a players position (in pixels) that is considered a warp (only while he is flashing).")]
        [ConfigHelp<CheckFastBombing>("Misc", "CheckFastBombing", ConfigScope.Arena, Default = CheckFastBombing.None,
            Description = "Fast bombing detection, can be a combination (sum) of the following:  1 - Send sysop alert when fastbombing is detected, 2 - Filter out fastbombs, 4 - Kick fastbombing player off.")]
        [ConfigHelp<int>("Misc", "FastBombingThreshold", ConfigScope.Arena, Default = 30,
            Description = "Tuning for fast bomb detection. A bomb/mine/thor is considered to be fast bombing if delay between 2 bombs/mines/thors is less than <ship>:BombFireDelay - Misc:FastBombingThreshold.")]
        [ConfigHelp<bool>("Prize", "DontShareThor", ConfigScope.Arena, Default = false,
            Description = "Whether Thor greens don't go to the whole team.")]
        [ConfigHelp<bool>("Prize", "DontShareBurst", ConfigScope.Arena, Default = false,
            Description = "Whether Burst greens don't go to the whole team.")]
        [ConfigHelp<bool>("Prize", "DontShareBrick", ConfigScope.Arena, Default = false,
            Description = "Whether Brick greens don't go to the whole team.")]
        [ConfigHelp<int>("Net", "BulletPixels", ConfigScope.Arena, Default = 1500,
            Description = "How far away to always send bullets (in pixels).")]
        [ConfigHelp<int>("Net", "WeaponPixels", ConfigScope.Arena, Default = 2000,
            Description = "How far away to always weapons (in pixels).")]
        [ConfigHelp<int>("Net", "PositionExtraPixels", ConfigScope.Arena, Default = 8000,
            Description = "How far away to to send positions of players on radar (in pixels).")]
        [ConfigHelp<int>("Net", "AntiwarpSendPercent", ConfigScope.Arena, Default = 5,
            Description = "Percent of position packets with antiwarp enabled to send to the whole arena.")]
        // Note: Toggle:AntiwarpPixels is a client setting, so its [ConfigHelp] is in ClientSettingsConfig.cs
        // Note: Kill:EnterDelay is a client setting, so its [ConfigHelp] is in ClientSettingsConfig.cs
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                    return;

                ConfigHandle ch = arena.Cfg!;

                ad.FlaggerKillMultiplier = _configManager.GetInt(ch, "Flag", "FlaggerKillMultiplier", 0);

                ad.RegionCheckTime = TimeSpan.FromMilliseconds(_configManager.GetInt(ch, "Misc", "RegionCheckInterval", ArenaSettings.Misc.RegionCheckInterval.Default) * 10);

                ad.SpecSeeExtra = _configManager.GetBool(ch, "Misc", "SpecSeeExtra", ArenaSettings.Misc.SpecSeeExtra.Default);

                ad.SpecSeeEnergy = _configManager.GetEnum(ch, "Misc", "SpecSeeEnergy", SeeEnergy.All);

                ad.AllSeeEnergy = _configManager.GetEnum(ch, "Misc", "SeeEnergy", SeeEnergy.None);

                ad.MaxDeathWithoutFiring = _configManager.GetInt(ch, "Security", "MaxDeathWithoutFiring", ArenaSettings.Security.MaxDeathWithoutFiring.Default);

                ad.NoSafeAntiWarp = _configManager.GetBool(ch, "Misc", "NoSafeAntiwarp", ArenaSettings.Misc.NoSafeAntiwarp.Default);

                ad.WarpThresholdDelta = _configManager.GetInt(ch, "Misc", "WarpThresholdDelta", ArenaSettings.Misc.WarpThresholdDelta.Default);
                ad.WarpThresholdDelta *= ad.WarpThresholdDelta;

                ad.CheckFastBombing = _configManager.GetEnum(ch, "Misc", "CheckFastBombing", CheckFastBombing.None);
                ad.FastBombingThreshold = (short)Math.Abs(_configManager.GetInt(ch, "Misc", "FastBombingThreshold", ArenaSettings.Misc.FastBombingThreshold.Default));

                PersonalGreen pg = PersonalGreen.None;

                if (_configManager.GetBool(ch, "Prize", "DontShareThor", ArenaSettings.Prize.DontShareThor.Default))
                    pg |= PersonalGreen.Thor;

                if (_configManager.GetBool(ch, "Prize", "DontShareBurst", ArenaSettings.Prize.DontShareBurst.Default))
                    pg |= PersonalGreen.Burst;

                if (_configManager.GetBool(ch, "Prize", "DontShareBrick", ArenaSettings.Prize.DontShareBrick.Default))
                    pg |= PersonalGreen.Brick;

                ad.PersonalGreen = pg;

                int cfg_bulletpix = _configManager.GetInt(ch, "Net", "BulletPixels", ArenaSettings.Net.BulletPixels.Default);

                int cfg_wpnpix = _configManager.GetInt(ch, "Net", "WeaponPixels", ArenaSettings.Net.WeaponPixels.Default);

                ad.PositionPixels = _configManager.GetInt(ch, "Net", "PositionExtraPixels", ArenaSettings.Net.PositionExtraPixels.Default);

                ad.SendAnti = _configManager.GetInt(ch, "Net", "AntiwarpSendPercent", ArenaSettings.Net.AntiwarpSendPercent.Default);
                ad.SendAnti = Constants.RandMax / 100 * ad.SendAnti;

                int cfg_AntiwarpPixels = _configManager.GetInt(ch, "Toggle", "AntiwarpPixels", 1);
                ad.AntiWarpRange = cfg_AntiwarpPixels * cfg_AntiwarpPixels;

                // continuum clients take EnterDelay + 100 ticks to respawn after death
                ad.EnterDelay = _configManager.GetInt(ch, "Kill", "EnterDelay", 0) + 100;
                // setting of 0 or less means respawn in place, with 1 second delay
                if (ad.EnterDelay <= 0)
                    ad.EnterDelay = 100;

                for (int x = 0; x < ad.WeaponRange.Length; x++)
                {
                    ad.WeaponRange[x] = cfg_wpnpix;
                }

                // exceptions
                ad.WeaponRange[(int)WeaponCodes.Bullet] = cfg_bulletpix;
                ad.WeaponRange[(int)WeaponCodes.BounceBullet] = cfg_bulletpix;
                ad.WeaponRange[(int)WeaponCodes.Thor] = 30000;

                if (action == ArenaAction.Create)
                    ad.InitLockShip = ad.InitSpec = false;
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                // clear the saved ppk, but set time to the present so that new
                // position packets look like they're in the future. also set a
                // bunch of other timers to now.
                pd.Position.Time = ServerTick.Now;
                pd.LastRegionCheck = DateTime.UtcNow;

                pd.LastRegionSet = [];

                pd.LockShip = ad.InitLockShip;
                if (ad.InitSpec)
                {
                    player.Ship = ShipType.Spec;
                    player.Freq = arena.SpecFreq;
                }

                player.Attached = -1;

                pd.LastPositionPacketShip = null;

                _playerData.Lock();

                try
                {
                    player.Flags.IsDead = false;
                    player.LastDeath = new ServerTick(); // TODO: review this, looks strange since 0 is a valid value, maybe need nullable?
                    player.NextRespawn = new ServerTick(); // TODO: review this, looks strange since 0 is a valid value, maybe need nullable?
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
            else if (action == PlayerAction.EnterArena)
            {
                SeeEnergy seeNrg = SeeEnergy.None;
                SeeEnergy seeNrgSpec = SeeEnergy.None;
                bool seeEpd = false;

                if (ad.AllSeeEnergy != SeeEnergy.None)
                    seeNrg = ad.AllSeeEnergy;

                if (ad.SpecSeeEnergy != SeeEnergy.None)
                    seeNrgSpec = ad.SpecSeeEnergy;

                if (ad.SpecSeeExtra)
                    seeEpd = true;

                if (_capabilityManager is not null)
                {
                    if (_capabilityManager.HasCapability(player, Constants.Capabilities.SeeEnergy))
                        seeNrg = seeNrgSpec = SeeEnergy.All;

                    if (_capabilityManager.HasCapability(player, Constants.Capabilities.SeeExtraPlayerData))
                        seeEpd = true;
                }

                pd.SeeNrg = seeNrg;
                pd.SeeNrgSpec = seeNrgSpec;
                pd.SeeEpd = seeEpd;
                pd.EpdPlayerWatchCount = 0;

                pd.DeathWithoutFiringCount = 0;
                player.Flags.SentWeaponPacket = false;
            }
            else if (action == PlayerAction.LeaveArena)
            {
                lock (_specLock)
                {
                    // Clear speccing state for any other players that were spectating the player that's leaving.
                    ClearSpeccingOfTarget(player);

                    if (pd.EpdPlayerWatchCount > 0)
                        _logManager.LogP(LogLevel.Error, nameof(Game), player, "Extra position data queries is still nonzero.");
                    
                    // Clear the speccing state for the player that's leaving (in case the player leaving was spectating someone else).
                    ClearSpeccing(player, pd, true);
                }

                pd.LastRegionSet = [];
            }
            else if (action == PlayerAction.EnterGame)
            {
                if (player.Ship != ShipType.Spec)
                {
                    DoSpawnCallback(player, SpawnCallback.SpawnReason.Initial);
                }
            }
        }

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (player is null)
                return;

            if (player.Type == ClientType.Fake && !isNew)
            {
                // Extra cleanup for fake players since LeaveArena isn't called.
                // Fake players can't be speccing anyone else, but other players can be speccing them.
                ClearSpeccingOfTarget(player);
            }
        }

        private void Callback_FlagGameReset(Arena arena, short winnerFreq, int points)
        {
            if (arena is null)
                return;

            if (winnerFreq == -1)
                return;

            // This assumes that the module that fired the callback sent the S2C_FlagReset packet to the arena.
            // Determine which players have been effectively ship reset by this flag reset.
            // Update their IsDead flags and invoke SpawnCallback.

            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena
                        && player.Freq == winnerFreq
                        && player.Ship != ShipType.Spec)
                    {
                        // Note: We could check if the player is in a safe zone too. (ASSS doesn't check it either)
                        // However, it's possible for the player to enter or leave a safe zone right before getting the S2C_FlagReset packet.
                        // So, no matter what, we can't guarantee being 100% accurate.
                        // We'll just assume that their ship was reset, regardless of being in a safe zone or not.

                        SpawnCallback.SpawnReason reason = SpawnCallback.SpawnReason.FlagVictory | SpawnCallback.SpawnReason.ShipReset;

                        if (player.Flags.IsDead)
                        {
                            player.Flags.IsDead = false;
                            reason |= SpawnCallback.SpawnReason.AfterDeath;
                        }

                        DoSpawnCallback(player, reason);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void ClearSpeccingOfTarget(Player targetPlayer)
        {
            lock (_specLock)
            {
                _playerData.Lock();
                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPlayerData))
                            continue;

                        if (otherPlayerData.Speccing == targetPlayer)
                            ClearSpeccing(otherPlayer, otherPlayerData, true);
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private void ClearSpeccing(Player player, PlayerData playerData, bool invokeCallback)
        {
            if (playerData is null)
                return;

            lock (_specLock)
            {
                Player? targetPlayer = playerData.Speccing;
                if (targetPlayer is null)
                    return;

                try
                {
                    if (playerData.SeeEpd)
                    {
                        if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                            return;

                        if (targetPlayerData.EpdPlayerWatchCount > 0)
                        {
                            targetPlayerData.EpdPlayerWatchCount--;

                            if (targetPlayerData.EpdWatchCount == 0)
                            {
                                // Tell the player that it no longer needs to to send extra position data.
                                SendSpecBytes(targetPlayer, false);
                            }
                        }
                    }
                }
                finally
                {
                    playerData.Speccing = null;

                    if (invokeCallback)
                        SpectateChangedCallback.Fire(player.Arena ?? _broker, player, null);
                }
            }
        }

        private void AddSpeccing(Player player, PlayerData playerData, Player targetPlayer)
        {
            lock (_specLock)
            {
                playerData.Speccing = targetPlayer;

                try
                {
                    if (playerData.SeeEpd)
                    {
                        if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                            return;

                        if (targetPlayerData.EpdWatchCount == 0)
                        {
                            // Tell the player to start sending extra position data.
                            SendSpecBytes(targetPlayer, true);
                        }

                        targetPlayerData.EpdPlayerWatchCount++;
                    }
                }
                finally
                {
                    SpectateChangedCallback.Fire(player.Arena ?? _broker, player, targetPlayer);
                }
            }
        }

        private void SendSpecBytes(Player player, bool sendExtraPositionData)
        {
            ReadOnlySpan<byte> specBytes = [(byte)S2CPacketType.SpecData, sendExtraPositionData ? (byte)1 : (byte)0];
            _network.SendToOne(player, specBytes, NetSendFlags.Reliable);
        }

        private void Packet_Position(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != C2S_PositionPacket.Length && data.Length != C2S_PositionPacket.LengthWithExtra)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad position packet (length={data.Length}).");
                return;
            }

            C2S_PositionPacket posCopy = MemoryMarshal.AsRef<C2S_PositionPacket>(data);

            if (data.Length >= C2S_PositionPacket.LengthWithExtra)
            {
                ExtraPositionData extraCopy = MemoryMarshal.AsRef<ExtraPositionData>(data.Slice(C2S_PositionPacket.Length, ExtraPositionData.Length));
                HandlePositionPacket(player, ref posCopy, ref extraCopy, true, false);
            }
            else
            {
                ExtraPositionData extraDummy = new();
                HandlePositionPacket(player, ref posCopy, ref extraDummy, false, false);
            }
        }

        private void HandlePositionPacket(Player player, ref C2S_PositionPacket pos, ref ExtraPositionData extra, bool hasExtra, bool isFake)
        {
            if (player is null || player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena is null || arena.Status != ArenaState.Running)
                return;

            // Verify checksum
            if (!isFake && !pos.IsValidChecksum)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, "Bad position packet checksum.");
                return;
            }

            if (pos.X == -1 && pos.Y == -1)
            {
                // position sent after death, before respawn. these aren't
                // really useful for anything except making sure the server
                // knows the client hasn't dropped, so just ignore them.
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            DateTime now = DateTime.UtcNow;
            ServerTick gtc = ServerTick.Now;

            // lag data
            if (!isFake)
            {
                _lagCollect.Position(
                    player,
                    (gtc - pos.Time) * 10,
                    hasExtra ? extra.S2CPing * 10 : new int?());
            }

            bool isNewer = pos.Time > playerData.Position.Time;
            bool isSafeZoneTransition = false;
            bool sendBounty = player.Position.Bounty != pos.Bounty // the player's bounty changed
                || playerData.BountyLastSent is null || (gtc - playerData.BountyLastSent > 200); // bounty not sent in the last 2 seconds

            // only copy if the new one is later
            if (isNewer || isFake)
            {
                // Safe zone
                isSafeZoneTransition = ((pos.Status ^ playerData.Position.Status) & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone;
                if (isSafeZoneTransition && !isFake)
                {
                    SafeZoneCallback.Fire(arena, player, pos.X, pos.Y, (pos.Status & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone);
                }

                // Warp
                if (((pos.Status ^ playerData.Position.Status) & PlayerPositionStatus.Flash) == PlayerPositionStatus.Flash
                    && !isFake
                    && player.Ship != ShipType.Spec
                    && player.Ship == playerData.LastPositionPacketShip
                    && player.Flags.SentPositionPacket
                    && !player.Flags.IsDead
                    && arenaData.WarpThresholdDelta > 0)
                {
                    int dx = playerData.Position.X - pos.X;
                    int dy = playerData.Position.Y - pos.Y;

                    if (dx * dx + dy * dy > arenaData.WarpThresholdDelta)
                    {
                        WarpCallback.Fire(arena, player, playerData.Position.X, playerData.Position.Y, pos.X, pos.Y);
                    }
                }

                // copy the whole thing. this will copy the epd, or, if the client
                // didn't send any epd, it will copy zeros because the buffer was
                // zeroed before data was recvd into it.
                playerData.Position = pos;

                // update position in global player object.
                // only copy x/y if they are nonzero, so we keep track of last
                // non-zero position.
                if (pos.X != 0 || pos.Y != 0)
                {
                    player.Position.X = pos.X;
                    player.Position.Y = pos.Y;
                }

                player.Position.XSpeed = pos.XSpeed;
                player.Position.YSpeed = pos.YSpeed;
                player.Position.Rotation = pos.Rotation;
                player.Position.Bounty = pos.Bounty;
                player.Position.Status = pos.Status;
                player.Position.Energy = pos.Energy;
                player.Position.Time = pos.Time;
            }

            // check if it's the player's first position packet
            if (player.Flags.SentPositionPacket == false && !isFake)
            {
                player.Flags.SentPositionPacket = true;
                PlayerActionCallback.Fire(arena, player, PlayerAction.EnterGame, arena);
            }

            int latency = gtc - pos.Time;
            if (latency < 0)
                latency = 0;
            else if (latency > 255)
                latency = 255;

            int randnum = _prng.Rand();

            // spectators don't get their position sent to anyone
            if (player.Ship != ShipType.Spec)
            {
                int x1 = pos.X;
                int y1 = pos.Y;

                // update region-based stuff once in a while, for real players only
                if (isNewer
                    && !isFake
                    && (now - playerData.LastRegionCheck) >= arenaData.RegionCheckTime)
                {
                    UpdateRegions(player, (short)(x1 >> 4), (short)(y1 >> 4));
                    playerData.LastRegionCheck = now;
                }

                // this check should be before the weapon ignore hook
                if (pos.Weapon.Type != WeaponCodes.Null)
                {
                    player.Flags.SentWeaponPacket = true;
                    playerData.DeathWithoutFiringCount = 0;
                }

                // Fast bombing check
                if (pos.Weapon.Type == WeaponCodes.Bomb || pos.Weapon.Type == WeaponCodes.ProxBomb || pos.Weapon.Type == WeaponCodes.Thor) // fired a bomb, mine, or thor
                {
                    if (arenaData.CheckFastBombing != CheckFastBombing.None
                        && playerData.LastBomb is not null)
                    { 
                        int bombDiff = Math.Abs(pos.Time - playerData.LastBomb.Value);
                        int minDiff = Math.Max(0, _clientSettings.GetSetting(player, _shipBombFireDelayIds[(int)player.Ship]) - arenaData.FastBombingThreshold);

                        if (minDiff > 0)
                        {
                            // Carrying a flag or a ball can modify bomb fire delay.
                            int flaggerBombFireDelay = _clientSettings.GetSetting(player, _flaggerBombFireDelayId);
                            if (flaggerBombFireDelay > 0)
                            {
                                int flaggerMinDiff = Math.Max(0, flaggerBombFireDelay - arenaData.FastBombingThreshold);
                                if (flaggerMinDiff > 0)
                                {
                                    if (player.Packet.FlagsCarried > 0
                                        || (_clientSettings.GetSetting(player, _soccerUseFlaggerId) != 0 && IsCarryingBall(player, arena)))
                                    {
                                        minDiff = Math.Min(minDiff, flaggerMinDiff);
                                    }
                                }
                            }
                        }

                        if (bombDiff < minDiff)
                        {
                            // Detected a fast bomb
                            bool alert = (arenaData.CheckFastBombing & CheckFastBombing.Alert) != 0;
                            bool filter = (arenaData.CheckFastBombing & CheckFastBombing.Filter) != 0;
                            bool kick = (arenaData.CheckFastBombing & CheckFastBombing.Kick) != 0;

                            _logManager.LogP(LogLevel.Info, nameof(Game), player, $"Detected fast bombing (diff:{bombDiff}, min:{minDiff}, alert:{alert}, filter:{filter}, kick:{kick}).");

                            if (alert)
                            {
                                _chat.SendModMessage($"Detected fast bombing by {player.Name} (diff:{bombDiff}, min:{minDiff}, filter:{filter}, kick:{kick}).");
                            }

                            if (filter)
                            {
                                pos.Weapon.Type = WeaponCodes.Null;
                            }

                            if (kick)
                            {
                                _playerData.KickPlayer(player);
                            }
                        }
                    }

                    if (playerData.LastBomb is null || pos.Time > playerData.LastBomb)
                    {
                        playerData.LastBomb = pos.Time;
                    }
                }

                // this is the weapons ignore hook.
                // also ignore weapons based on region
                if ((_prng.Rand() < playerData.IgnoreWeapons)
                    || playerData.MapRegionNoWeapons)
                {
                    pos.Weapon.Type = WeaponCodes.Null;
                }

                // also turn off anti based on region
                if (playerData.MapRegionNoAnti)
                {
                    pos.Status &= ~PlayerPositionStatus.Antiwarp;
                }

                // if this is a plain position packet with no weapons, and is in
                // the wrong order, there's no need to send it. but fake players
                // never got data->pos.time initialized correctly, so do a
                // little special case.
                if (!isNewer && !isFake && pos.Weapon.Type == WeaponCodes.Null)
                    return;

                // Consult the player position advisors to allow other modules to edit the packet.
                var advisors = arena.GetAdvisors<IPlayerPositionAdvisor>();
                foreach (var advisor in advisors)
                {
                    advisor.EditPositionPacket(player, ref pos);

                    // Allow advisors to drop the position packet.
                    if (pos.X < 0 || pos.Y < 0) // slightly different than ASSS, here we consider anything negative to mean drop whereas ASSS looks for -1
                        return;
                }

                // by default, send unreliable droppable packets. 
                // weapons get a higher priority.
                NetSendFlags sendFlags = NetSendFlags.Unreliable | NetSendFlags.Droppable |
                    (pos.Weapon.Type != WeaponCodes.Null ? NetSendFlags.PriorityP5 : NetSendFlags.PriorityP3);

                // TODO: for arenas designed for a small # of players (e.g. 4v4 league), a way to always send to all? or is boosting weapon range settings to max good enough?
                bool sendToAll = false;

                // TODO: if a player is far away from the mine, how would that player know if the mine was cleared (detonated by another player outside of their range or repelled)?
                // would need a module to keep track of mine locations and do pseudo-region like comparisons? and use that know when to send a position packet to all?
                // what about bombs fired at low velocity? need wall collision detection?
                // TODO: a way for the server to keep track of where mines are currently placed so that it can replay packets to players that enter the arena? or does something like this exist already?

                // send mines to everyone
                if ((pos.Weapon.Type == WeaponCodes.Bomb || pos.Weapon.Type == WeaponCodes.ProxBomb)
                    && pos.Weapon.Alternate)
                {
                    sendToAll = true;
                }

                // disable antiwarp if they're in a safe and NoSafeAntiwarp is on
                if ((pos.Status & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp
                    && (pos.Status & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone
                    && arenaData.NoSafeAntiWarp)
                {
                    pos.Status &= ~PlayerPositionStatus.Antiwarp;
                }

                // send some percent of antiwarp positions to everyone
                if ((pos.Weapon.Type == WeaponCodes.Null)
                    && ((pos.Status & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp)
                    && (_prng.Rand() < arenaData.SendAnti))
                {
                    sendToAll = true;
                }

                // Send safe zone transitions to everyone, reliably.
                // Sending when a player enters a safe zone signals to clear that player's weapons (especially mines).
                // Also, knowing whether a player is in a safe zone helps to keep scores in sync.
                // Certain events (flag game victory, periodic reward, ball goal) can add points to a team.
                // The client knows to not add points to players that are in a safe zone.
                if (isSafeZoneTransition)
                {
                    sendToAll = true;
                    sendFlags = NetSendFlags.Reliable;
                }

                // Send flashes to everyone, reliably.
                // TODO: Review this, does it need to be sent reliably? probably better to send urgently (portal usage)
                // Why send to all? Possibly as an easy way to send it to players in the previous location. So this probably can be enhanced to only send to those that need to know.
                // If we really want to try to ensure that the flash is seen, maybe send one packet urgently, and a duplicate reliably.
                // I'm not sure if Continuum will just ignore the dup reliable packet? or if it would flash again? Would need to test.
                if ((pos.Status & PlayerPositionStatus.Flash) != 0)
                {
                    sendToAll = true;
                    sendFlags = NetSendFlags.Reliable;
                }

                C2S_PositionPacket posCopy = new();
                ExtraPositionData extraCopy = new();
                S2C_BatchedSmallPositionSingle smallSingle = new();
                S2C_BatchedLargePositionSingle largeSingle = new();
                S2C_WeaponsPacket wpn = new();
                S2C_PositionPacket sendPos = new();

                // ensure that all packets get build before use
                bool modified = true;
                bool smallDirty = true;
                bool largeDirty = true;
                bool wpnDirty = true;
                bool posDirty = true;

                _playerData.Lock();

                try
                {
                    // have to do this check inside pd->Lock();
                    // ignore packets from the first 500ms of death, and accept packets up to 500ms
                    // before their expected respawn. 
                    if (player.Flags.IsDead && gtc - player.LastDeath >= 50 && player.NextRespawn - gtc <= 50)
                    {
                        player.Flags.IsDead = false;
                        DoSpawnCallback(player, SpawnCallback.SpawnReason.AfterDeath);
                    }

                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? otherPlayerData))
                            continue;

                        if (otherPlayer.Status == PlayerState.Playing
                            && otherPlayer.IsStandard
                            && otherPlayer.Arena == arena
                            && (otherPlayer != player || player.Flags.SeeOwnPosition))
                        {
                            int dist = Hypot(x1 - otherPlayerData.Position.X, y1 - otherPlayerData.Position.Y);
                            int range;

                            // determine the packet range
                            if (pos.Weapon.Type != WeaponCodes.Null)
                                range = Math.Max(arenaData.WeaponRange[(int)pos.Weapon.Type], otherPlayer.Xres + otherPlayer.Yres);
                            else
                                range = otherPlayer.Xres + otherPlayer.Yres;

                            if (dist <= range
                                || sendToAll
                                || otherPlayerData.Speccing == player // always send to spectators of the player
                                || otherPlayer.Attached == player.Id // always send to turreters
                                || (pos.Weapon.Type == WeaponCodes.Null
                                    && dist < arenaData.PositionPixels
                                    && randnum > ((double)dist / (double)arenaData.PositionPixels * Constants.RandMax + 1d)) // send some radar packets
                                || otherPlayer.Flags.SeeAllPositionPackets) // bots
                            {
                                int extraLength;

                                const int plainLength = 0;
                                const int energyLength = 2;

                                if (otherPlayer.Ship == ShipType.Spec)
                                {
                                    if (otherPlayerData.SeeEpd && otherPlayerData.Speccing == player)
                                    {
                                        extraLength = hasExtra ? ExtraPositionData.Length : energyLength;
                                    }
                                    else if (otherPlayerData.SeeNrgSpec == SeeEnergy.All
                                        || (otherPlayerData.SeeNrgSpec == SeeEnergy.Team
                                            && player.Freq == otherPlayer.Freq)
                                        || (otherPlayerData.SeeNrgSpec == SeeEnergy.Spec
                                            && otherPlayerData.Speccing == player))
                                    {
                                        extraLength = energyLength;
                                    }
                                    else
                                    {
                                        extraLength = plainLength;
                                    }
                                }
                                else if (otherPlayerData.SeeNrg == SeeEnergy.All
                                    || (otherPlayerData.SeeNrg == SeeEnergy.Team
                                        && player.Freq == otherPlayer.Freq))
                                {
                                    extraLength = energyLength;
                                }
                                else
                                {
                                    extraLength = plainLength;
                                }

                                if (modified)
                                {
                                    posCopy = pos;
                                    extraCopy = extra;
                                    modified = false;
                                }

                                bool drop = false;

                                // Consult the advisors to allow other modules to edit or drop the packet.
                                foreach (var advisor in advisors)
                                {
                                    if (advisor.EditIndividualPositionPacket(player, otherPlayer, ref posCopy, ref extraCopy, ref extraLength))
                                        modified = true;

                                    // Allow advisors to drop the packet.
                                    if (posCopy.X < 0 || posCopy.Y < 0) // slightly different than ASSS, here we consider anything negative to mean drop whereas ASSS looks for -1
                                    {
                                        drop = true;
                                        break;
                                    }
                                }

                                if (!drop)
                                {
                                    if ((otherPlayer.ClientFeatures & ClientFeatures.BatchPositions) != 0
                                        && posCopy.Weapon.Type == WeaponCodes.Null
                                        && !sendBounty
                                        && posCopy.Status == 0
                                        && extraLength == 0 // no energy or extra data
                                        && ((uint)player.Id & 0xFFFF_FF00) == 0 // PlayerId [0-255]
                                        && posCopy.XSpeed >= -8192 && posCopy.XSpeed <= 8191
                                        && posCopy.YSpeed >= -8192 && posCopy.YSpeed <= 8191
                                        && posCopy.X >= 0 && posCopy.X <= 16383
                                        && posCopy.Y >= 0 && posCopy.Y <= 16383)
                                    {
                                        // 0x39 Small
                                        if (smallDirty || modified)
                                        {
                                            ref SmallPosition small = ref smallSingle.Position;
                                            small.PlayerId = (byte)player.Id;
                                            small.Rotation = posCopy.Rotation;
                                            small.Time = (ushort)(gtc - (uint)latency);
                                            small.X = posCopy.X;
                                            small.Y = posCopy.Y;
                                            small.XSpeed = posCopy.XSpeed;
                                            small.YSpeed = posCopy.YSpeed;

                                            smallDirty = modified;
                                        }

                                        _network.SendToOne(otherPlayer, ref smallSingle, sendFlags);
                                    }
                                    else if ((otherPlayer.ClientFeatures & ClientFeatures.BatchPositions) != 0
                                        && posCopy.Weapon.Type == WeaponCodes.Null
                                        && !sendBounty
                                        && ((int)posCopy.Status & 0b11000000) == 0 // only the 6 lower bits
                                        && extraLength == 0 // no energy or extra data
                                        && ((uint)player.Id & 0xFFFF_FC00) == 0 // PlayerId [0-1023]
                                        && posCopy.XSpeed >= -8192 && posCopy.XSpeed <= 8191
                                        && posCopy.YSpeed >= -8192 && posCopy.YSpeed <= 8191
                                        && posCopy.X >= 0 && posCopy.X <= 16383
                                        && posCopy.Y >= 0 && posCopy.Y <= 16383)
                                    {
                                        // 0x3A Large
                                        if (largeDirty || modified)
                                        {
                                            ref LargePosition large = ref largeSingle.Position;
                                            large.Status = posCopy.Status;
                                            large.PlayerId = (ushort)player.Id;
                                            large.Rotation = posCopy.Rotation;
                                            large.Time = (ushort)(gtc - (uint)latency);
                                            large.X = posCopy.X;
                                            large.Y = posCopy.Y;
                                            large.XSpeed = posCopy.XSpeed;
                                            large.YSpeed = posCopy.YSpeed;

                                            largeDirty = modified;
                                        }

                                        _network.SendToOne(otherPlayer, ref largeSingle, sendFlags);
                                    }
                                    else if (posCopy.Weapon.Type > 0 // has weapon fire
                                        || (posCopy.Bounty & 0xFF00) != 0 // bounty over 255
                                        || ((uint)player.Id & 0xFFFF_FF00) != 0) // PlayerId over 255
                                    {
                                        // 0x05 Weapon
                                        if (wpnDirty || modified)
                                        {
                                            wpn.Type = (byte)S2CPacketType.Weapon;
                                            wpn.Rotation = posCopy.Rotation;
                                            wpn.Time = (ushort)(gtc & 0xFFFF);
                                            wpn.X = posCopy.X;
                                            wpn.YSpeed = posCopy.YSpeed;
                                            wpn.PlayerId = (ushort)player.Id;
                                            wpn.XSpeed = posCopy.XSpeed;
                                            wpn.Checksum = 0;
                                            wpn.Status = posCopy.Status;
                                            wpn.C2SLatency = (byte)latency;
                                            wpn.Y = posCopy.Y;
                                            wpn.Bounty = posCopy.Bounty;
                                            wpn.Weapon = posCopy.Weapon;
                                            wpn.Extra = extraCopy;

                                            // move this field from the main packet to the extra data, in case they don't match.
                                            wpn.Extra.Energy = (ushort)posCopy.Energy;

                                            wpnDirty = modified;

                                            wpn.SetChecksum();
                                        }

                                        if (wpn.Weapon.Type != 0)
                                        {
                                            _lagCollect.IncrementWeaponSentCount(otherPlayer);
                                        }

                                        ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref wpn, 1));
                                        int length = S2C_WeaponsPacket.Length + extraLength;
                                        if (data.Length > length)
                                            data = data[..length];

                                        _network.SendToOne(otherPlayer, data, sendFlags);
                                    }
                                    else
                                    {
                                        // 0x28 Position
                                        if (posDirty || modified)
                                        {
                                            sendPos.Type = (byte)S2CPacketType.Position;
                                            sendPos.Rotation = posCopy.Rotation;
                                            sendPos.Time = (ushort)(gtc & 0xFFFF);
                                            sendPos.X = posCopy.X;
                                            sendPos.C2SLatency = (byte)latency;
                                            sendPos.Bounty = (byte)posCopy.Bounty;
                                            sendPos.PlayerId = (byte)player.Id;
                                            sendPos.Status = posCopy.Status;
                                            sendPos.YSpeed = posCopy.YSpeed;
                                            sendPos.Y = posCopy.Y;
                                            sendPos.XSpeed = posCopy.XSpeed;
                                            sendPos.Extra = extraCopy;

                                            // move this field from the main packet to the extra data, in case they don't match.
                                            sendPos.Extra.Energy = (ushort)posCopy.Energy;

                                            posDirty = modified;
                                        }

                                        ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref sendPos, 1));
                                        int length = S2C_PositionPacket.Length + extraLength;
                                        if (data.Length > length)
                                            data = data[..length];

                                        _network.SendToOne(otherPlayer, data, sendFlags);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                PlayerPositionPacketCallback.Fire(arena, player, ref pos, ref extra, hasExtra);
            }

            playerData.LastPositionPacketShip = player.Ship;

            if (sendBounty)
            {
                playerData.BountyLastSent = gtc;
            }

            // local function that checks if a player is carrying a ball
            bool IsCarryingBall(Player player, Arena arena)
            {
                IBalls? balls = _broker.GetInterface<IBalls>();
                if (balls is null)
                    return false;

                try
                {
                    if (!balls.TryGetBallSettings(arena, out BallSettings ballSettings))
                        return false;

                    int ballCount = ballSettings.BallCount;
                    for (int ballId = 0; ballId < ballCount; ballId++)
                    {
                        if (!balls.TryGetBallData(arena, ballId, out BallData ballData))
                            continue;

                        if (ballData.State == BallState.Carried && ballData.Carrier == player)
                            return true;
                    }

                    return false;
                }
                finally
                {
                    _broker.ReleaseInterface(ref balls);
                }
            }
        }

        private void UpdateRegions(Player player, short x, short y)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ImmutableHashSet<MapRegion> oldRegions = pd.LastRegionSet;
            ImmutableHashSet<MapRegion> newRegions = _mapData.RegionsAt(arena, x, y);

            pd.MapRegionNoAnti = pd.MapRegionNoWeapons = false;

            foreach (MapRegion region in newRegions)
            {
                if (region.NoAntiwarp)
                    pd.MapRegionNoAnti = true;

                if (region.NoWeapons)
                    pd.MapRegionNoWeapons = true;

                if (!oldRegions.Contains(region))
                {
                    MapRegionCallback.Fire(arena, player, region, x, y, true); // entered region
                }
            }

            foreach (MapRegion region in oldRegions)
            {
                if (!newRegions.Contains(region))
                {
                    MapRegionCallback.Fire(arena, player, region, x, y, false); // exited region
                }
            }

            pd.LastRegionSet = newRegions;
        }

        private void Packet_SpecRequest(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != C2S_SpecRequest.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad spec req packet (length={data.Length}).");
                return;
            }

            if (player is null || player.Status != PlayerState.Playing || player.Ship != ShipType.Spec)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            ref readonly C2S_SpecRequest packet = ref MemoryMarshal.AsRef<C2S_SpecRequest>(data[..C2S_SpecRequest.Length]);
            int targetPlayerId = packet.PlayerId;

            lock (_specLock)
            {
                Player? targetPlayer = null;
                if (targetPlayerId >= 0)
                {
                    targetPlayer = _playerData.PidToPlayer(targetPlayerId);
                    if (targetPlayer is not null
                        && (targetPlayer.Status != PlayerState.Playing || targetPlayer.Ship == ShipType.Spec || targetPlayer.Arena != player.Arena))
                    {
                        targetPlayer = null;
                    }
                }
                
                ClearSpeccing(player, playerData, targetPlayer is null);

                if (targetPlayer is not null)
                {
                    AddSpeccing(player, playerData, targetPlayer);
                }
            }
        }

        private void Packet_SetShip(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != 2)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad ship req packet (length={data.Length}).");
                return;
            }

            Arena? arena = player.Arena;
            if (player.Status != PlayerState.Playing || arena is null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), player, "State sync problem: Ship request from bad status.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            ShipType ship = (ShipType)data[1];
            if (ship < ShipType.Warbird || ship > ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad ship number: {ship}.");
                return;
            }

            short freq = player.Freq;

            lock (_freqShipLock)
            {
                if (player.Flags.DuringChange)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), player, "State sync problem: Ship request before ack from previous change.");
                    return;
                }

                if (ship == player.Ship)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), player, "State sync problem: Already in requested ship.");
                    return;
                }

                // do this bit while holding the mutex. it's ok to check the flag afterwards, though.
                ExpireLock(player);
            }

            // checked lock state (but always allow switching to spec)
            if (pd.LockShip 
                && ship != ShipType.Spec 
                && !(_capabilityManager is not null && _capabilityManager.HasCapability(player, Constants.Capabilities.BypassLock)))
            {
                _chat.SendMessage(player, $"You have been locked in {(player.Ship == ShipType.Spec ? "spectator mode" : "your ship")}.");
                return;
            }

            IFreqManager? freqManager = arena.GetInterface<IFreqManager>();
            if (freqManager is not null)
            {
                try
                {
                    StringBuilder errorBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        freqManager.ShipChange(player, ship, errorBuilder);

                        if (errorBuilder.Length > 0)
                        {
                            _chat.SendMessage(player, errorBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(errorBuilder);
                    }
                }
                finally
                {
                    arena.ReleaseInterface(ref freqManager);
                }
            }
            else
            {
                SetShipAndFreq(player, ship, freq);
            }
        }

        private void Packet_SetFreq(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != C2S_SetFreq.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad freq req packet (length={data.Length}).");
            }
            else if (player.Flags.DuringChange)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), player, "State sync problem: Freq change before ack from previous change.");
            }
            else
            {
                ref readonly C2S_SetFreq packet = ref MemoryMarshal.AsRef<C2S_SetFreq>(data);
                FreqChangeRequest(player, packet.Freq);
            }
        }

        private void FreqChangeRequest(Player player, short freq)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            Arena? arena = player.Arena;
            ShipType ship = player.Ship;

            if (player.Status != PlayerState.Playing || arena is null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, "Freq change from bad arena.");
                return;
            }

            // check lock state
            lock (_freqShipLock)
            {
                ExpireLock(player);
            }

            if (pd.LockShip
                && !(_capabilityManager is not null && _capabilityManager.HasCapability(player, Constants.Capabilities.BypassLock)))
            {
                _chat.SendMessage(player, $"You have been locked in {(player.Ship == ShipType.Spec ? "spectator mode" : "your ship")}.");
                return;
            }

            IFreqManager? freqManager = _broker.GetInterface<IFreqManager>();
            if (freqManager is not null)
            {
                try
                {
                    StringBuilder errorBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        freqManager.FreqChange(player, freq, errorBuilder);

                        if (errorBuilder.Length > 0)
                        {
                            _chat.SendMessage(player, errorBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(errorBuilder);
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref freqManager);
                }
            }
            else
            {
                SetFreq(player, freq);
            }
        }

        private void SetShipAndFreq(Player player, ShipType ship, short freq)
        {
            if (freq < 0 || freq > 9999 || ship < 0 || ship > ShipType.Spec)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (player.Type == ClientType.Chat && ship != ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), player, "Attempted to force a chat client into a playing ship.");
                return;
            }

            ShipType oldShip = player.Ship;
            short oldFreq = player.Freq;

            // Before we set the ship and/or freq, allow other modules to do something other than changing ship or freq.
            BeforeShipFreqChangeCallback.Fire(arena, player, ship, oldShip, freq, oldFreq);

            lock (_freqShipLock)
            {
                if (player.Ship == ship && player.Freq == freq)
                {
                    // nothing to do
                    return;
                }

                if (player.IsStandard)
                    player.Flags.DuringChange = true;

                player.Ship = ship;
                player.Freq = freq;

                lock (_specLock)
                {
                    // Even if the player switched to spectator mode, we purposely do not clear the speccing state of players that were spectating the player.
                    // See PlayerData.Speccing for more info.

                    if (oldShip == ShipType.Spec && ship != ShipType.Spec)
                    {
                        // The player switched from spectator mode into a ship.
                        // Clear the speccing state (in case the player was spectating someone else).
                        ClearSpeccing(player, playerData, true);
                    }
                }
            }

            S2C_ShipChange packet = new((sbyte)ship, (short)player.Id, freq);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

            if (player.IsStandard)
            {
                // send it to him, with a callback
                _network.SendWithCallback(player, data, ResetDuringChange);
            }

            // send it to everyone else
            _network.SendToArena(arena, player, data, NetSendFlags.Reliable);
            _chatNetwork?.SendToArena(arena, null, $"SHIPFREQCHANGE:{player.Name}:{ship:D}:{freq:D}");

            PreShipFreqChangeCallback.Fire(arena, player, ship, oldShip, freq, oldFreq);
            DoShipFreqChangeCallback(player, ship, oldShip, freq, oldFreq);

            // now setup for the CB_SPAWN callback
            _playerData.Lock();

            SpawnCallback.SpawnReason flags = SpawnCallback.SpawnReason.ShipChange;
            try
            {
                if (player.Flags.IsDead)
                {
                    flags |= SpawnCallback.SpawnReason.AfterDeath;
                }

                // a ship change will revive a dead player
                player.Flags.IsDead = false;
            }
            finally
            {
                _playerData.Unlock();
            }

            if (ship != ShipType.Spec)
            {
                // flags = SpawnReason.ShipChange set at the top of the function
                if (oldShip == ShipType.Spec)
                {
                    flags |= SpawnCallback.SpawnReason.Initial;
                }

                DoSpawnCallback(player, flags);
            }

            _logManager.LogP(LogLevel.Info, nameof(Game), player, $"Changed ship/freq to ship {ship}, freq {freq}.");
        }

        private void SetFreq(Player player, short freq)
        {
            if (player is null)
                return;

            if (freq < 0 || freq > 9999)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            short oldFreq = player.Freq;

            lock (_freqShipLock)
            {
                if (player.Freq == freq)
                    return;

                if (player.IsStandard)
                    player.Flags.DuringChange = true;

                player.Freq = freq;
            }

            S2C_FreqChange packet = new((short)player.Id, freq);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

            // him with callback
            if (player.IsStandard)
                _network.SendWithCallback(player, data, ResetDuringChange);

            // everyone else
            _network.SendToArena(arena, player, data, NetSendFlags.Reliable);
            _chatNetwork?.SendToArena(arena, null, $"SHIPFREQCHANGE:{player.Name}:{player.Ship:D}:{freq:D}");

            PreShipFreqChangeCallback.Fire(arena, player, player.Ship, player.Ship, freq, oldFreq);
            DoShipFreqChangeCallback(player, player.Ship, player.Ship, freq, oldFreq);

            _logManager.LogP(LogLevel.Info, nameof(Game), player, $"Changed freq to {freq}.");
        }

        private void ResetDuringChange(Player player, bool success)
        {
            if (player is null)
                return;

            lock (_freqShipLock)
            {
                player.Flags.DuringChange = false;
            }
        }

        private void ExpireLock(Player player)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (_freqShipLock)
            {
                if (pd.Expires is not null)
                    if (DateTime.UtcNow > pd.Expires)
                    {
                        pd.LockShip = false;
                        pd.Expires = null;
                        _logManager.LogP(LogLevel.Drivel, nameof(Game), player, "Lock expired.");
                    }
            }
        }

        [ConfigHelp<bool>("Prize", "UseTeamkillPrize", ConfigScope.Arena, Default = false,
            Description = "Whether to use a special prize for teamkills. Prize:TeamkillPrize specifies the prize #.")]
        [ConfigHelp<int>("Prize", "TeamkillPrize", ConfigScope.Arena, Default = 0,
            Description = "The prize # to give for a teamkill, if Prize:UseTeamkillPrize=1.")]
        private void Packet_Die(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != C2S_Die.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad death packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ref readonly C2S_Die packet = ref MemoryMarshal.AsRef<C2S_Die>(data[..C2S_Die.Length]);
            short bounty = packet.Bounty;

            Player? killer = _playerData.PidToPlayer(packet.Killer);
            if (killer is null || killer.Status != PlayerState.Playing || killer.Arena != arena)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Reported kill by bad pid {packet.Killer}.");
                return;
            }

            short flagCount = player.Packet.FlagsCarried;

            _playerData.Lock();

            try
            {
                // these flags are primarily for the benefit of other modules
                player.Flags.IsDead = true;
                player.LastDeath = ServerTick.Now;
                player.NextRespawn = player.LastDeath + (uint)ad.EnterDelay;
            }
            finally
            {
                _playerData.Unlock();
            }

            var killAdvisors = arena.GetAdvisors<IKillAdvisor>();

            // Consult the advisors after setting the above flags, the flags reflect the real state of the player.
            foreach (var advisor in killAdvisors)
            {
                advisor.EditDeath(arena, ref killer, ref player, ref bounty);

                if (player is null || killer is null)
                    return; // The advisor wants to drop the kill packet.

                if (player.Status != PlayerState.Playing || player.Arena != arena)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Game), player, $"An {nameof(IKillAdvisor)} set killed to a bad player.");
                    return;
                }

                if (killer.Status != PlayerState.Playing || killer.Arena != arena)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Game), killer, $"An {nameof(IKillAdvisor)} set killer to a bad player.");
                    return;
                }
            }

            // Pick the green.
            Prize green;
            if ((player.Freq == killer.Freq) && (_configManager.GetBool(arena.Cfg!, "Prize", "UseTeamkillPrize", ArenaSettings.Prize.UseTeamkillPrize.Default)))
            {
                green = (Prize)_configManager.GetInt(arena.Cfg!, "Prize", "TeamkillPrize", ArenaSettings.Prize.TeamkillPrize.Default);
            }
            else
            {
                // Pick a random green.
                IClientSettings? clientSettings = arena.GetInterface<IClientSettings>();
                if (clientSettings is not null)
                {
                    try
                    {
                        green = clientSettings.GetRandomPrize(arena);
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref clientSettings);
                    }
                }
                else
                {
                    green = 0;
                }
            }

            // Use advisors to determine how many points to award.
            short points = 0;
            foreach (var advisor in killAdvisors)
            {
                points += advisor.KillPoints(arena, killer, player, bounty, flagCount);
            }

            // Allow a module to modify the green sent in the packet.
            IKillGreen? killGreen = arena.GetInterface<IKillGreen>();
            if (killGreen is not null)
            {
                try
                {
                    green = killGreen.KillGreen(arena, killer, player, bounty, flagCount, points, green);
                }
                finally
                {
                    arena.ReleaseInterface(ref killGreen);
                }
            }

            // Find out how many flags will get transferred to the killer.
            ICarryFlagGame? carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            try
            {
                short flagTransferCount = (flagCount > 0 && carryFlagGame is not null)
                    ? carryFlagGame.GetPlayerKillTransferCount(arena, player, killer)
                    : (short)0;

                // Send the S2C Kill packet.
                NotifyKill(killer, player, points, flagTransferCount, green);

                if (points > 0)
                {
                    if (killer.Packet.FlagsCarried > 0
                        && ad.FlaggerKillMultiplier > 0)
                    {
                        // This is purposely after NotifyKill because Flag:FlaggerKillMultiplier is a client setting.
                        // Clients multiply the points from the S2C Kill packet that we just sent.
                        points += (short)(points * ad.FlaggerKillMultiplier);
                    }

                    // Record the kill points on our side.
                    IAllPlayerStats? allPlayerStats = _broker.GetInterface<IAllPlayerStats>();
                    if (allPlayerStats is not null)
                    {
                        try
                        {
                            allPlayerStats.IncrementStat(killer, StatCodes.KillPoints, null, points);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref allPlayerStats);
                        }
                    }
                }

                KillCallback.Fire(arena, arena, killer, player, bounty, flagTransferCount, points, green);

                _logManager.LogA(LogLevel.Info, nameof(Game), arena, $"{player.Name} killed by {killer.Name} (bty={bounty},flags={flagTransferCount},pts={points})");

                if (!player.Flags.SentWeaponPacket)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    {
                        if (pd.DeathWithoutFiringCount++ == ad.MaxDeathWithoutFiring)
                        {
                            _logManager.LogP(LogLevel.Info, nameof(Game), player, "Specced for too many deaths without firing.");
                            SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                        }
                    }
                }

                // reset this so we can accurately check deaths without firing
                player.Flags.SentWeaponPacket = false;

                // Perform the actual flag transfer.
                // This is purposely done after sending the S2C Kill packet as it could trigger the end of a flag game (send the S2C flag reset packet).
                if (flagCount > 0 && carryFlagGame is not null)
                    carryFlagGame.TransferFlagsForPlayerKill(arena, player, killer);
            }
            finally
            {
                if (carryFlagGame is not null)
                    arena.ReleaseInterface(ref carryFlagGame);
            }
        }

        private void NotifyKill(Player killer, Player killed, short pts, short flagCount, Prize green)
        {
            if (killer is null || killed is null)
                return;

            Arena? arena = killed.Arena;
            if (arena is null)
                return;

            S2C_Kill packet = new(green, (short)killer.Id, (short)killed.Id, pts, flagCount);
            _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable);
            _chatNetwork?.SendToArena(arena, null, $"KILL:{killer.Name}:{killed.Name}:{pts:D}:{flagCount:D}");
        }

        private void Packet_Green(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != C2S_Green.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad green packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ref readonly C2S_Green c2s = ref MemoryMarshal.AsRef<C2S_Green>(data);
            Prize prize = c2s.Prize;

            // don't forward non-shared prizes
            if (!(prize == Prize.Thor && (ad.PersonalGreen & PersonalGreen.Thor) == PersonalGreen.Thor)
                && !(prize == Prize.Burst && (ad.PersonalGreen & PersonalGreen.Burst) == PersonalGreen.Burst)
                && !(prize == Prize.Brick && (ad.PersonalGreen & PersonalGreen.Brick) == PersonalGreen.Brick))
            {
                S2C_Green s2c = new(in c2s, (short)player.Id);
                _network.SendToArena(arena, player, ref s2c, NetSendFlags.Unreliable);
            }

            FireGreenEvent(arena, player, c2s.X, c2s.Y, prize);
        }

        private void FireGreenEvent(Arena arena, Player player, int x, int y, Prize prize)
        {
            if (player is null)
                return;

            if (arena is not null)
                GreenCallback.Fire(arena, player, x, y, prize);
            else
                GreenCallback.Fire(_broker, player, x, y, prize);
        }

        // Note: This method assumes all validation checks have been done beforehand (playing, same arena, same team, etc...).
        private void Attach(Player player, Player? to)
        {
            if (player is null)
                return;

            short toPlayerId = (short)(to is not null ? to.Id : -1);

            // only if state has changed
            if (player.Attached != toPlayerId)
            {
                // Send the packet
                S2C_Turret packet = new((short)player.Id, toPlayerId);
                _network.SendToArena(player.Arena, null, ref packet, NetSendFlags.Reliable);

                // Update the state
                player.Attached = toPlayerId;

                // Invoke the callback
                Arena? arena = player.Arena;
                if (arena is null)
                    return;

                _mainloop.QueueMainWorkItem(
                    MainloopWork_FireAttachCallback,
                    new AttachDTO()
                    {
                        Arena = arena,
                        Player = player,
                        To = to,
                    });
            }

            static void MainloopWork_FireAttachCallback(AttachDTO dto)
            {
                if (dto.Arena == dto.Player.Arena)
                {
                    AttachCallback.Fire(dto.Arena, dto.Player, dto.To);
                }
            }
        }

        private void Packet_AttachTo(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data.Length != C2S_AttachTo.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad attach req packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ref readonly C2S_AttachTo packet = ref MemoryMarshal.AsRef<C2S_AttachTo>(data[..C2S_AttachTo.Length]);
            short pid2 = packet.PlayerId;

            Player? to = null;

            // -1 means detaching
            if (pid2 != -1)
            {
                to = _playerData.PidToPlayer(pid2);

                if (to is null
                    || to == player
                    || to.Status != PlayerState.Playing
                    || player.Arena != to.Arena
                    || player.Freq != to.Freq)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Tried to attach to bad pid {pid2}.");
                    return;
                }
            }

            Attach(player, to);
        }

        private void Packet_TurretKickoff(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), player, $"Bad turret kickoff packet (length={data.Length}).");
                return;
            }

            ((IGame)this).TurretKickoff(player);
        }

        private static int Hypot(int dx, int dy)
        {
            uint dd = (uint)((dx * dx) + (dy * dy));

            if (dx < 0) dx = -dx;
            if (dy < 0) dy = -dy;

            // initial hypotenuse guess (from Gems)
            uint r = (uint)((dx > dy) ? (dx + (dy >> 1)) : (dy + (dx >> 1)));

            if (r == 0) return (int)r;

            // converge 3 times
            r = (dd / r + r) >> 1;
            r = (dd / r + r) >> 1;
            r = (dd / r + r) >> 1;

            return (int)r;
        }

        private void LockWork(ITarget target, bool newValue, bool notify, bool spec, int timeout)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);

                foreach (Player player in set)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                        continue;

                    if (spec && (player.Arena is not null) && (player.Ship != ShipType.Spec))
                        SetShipAndFreq(player, ShipType.Spec, player.Arena.SpecFreq);

                    if (notify && (playerData.LockShip != newValue))
                    {
                        _chat.SendMessage(player, newValue ?
                            (player.Ship == ShipType.Spec ?
                            "You have been locked to spectator mode." :
                            "You have been locked to your ship.") :
                            "Your ship has been unlocked.");
                    }

                    playerData.LockShip = newValue;
                    if (newValue == false || timeout == 0)
                        playerData.Expires = null;
                    else
                        playerData.Expires = DateTime.UtcNow.AddSeconds(timeout);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void ChatHandler_ChangeFreq(Player player, ReadOnlySpan<char> message)
        {
            if (short.TryParse(message, out short freq))
            {
                FreqChangeRequest(player, freq);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description = """
                Displays players spectating you.
                When sent private, displays players spectating the target.
                """)]
        private void Command_spec(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                targetPlayer = player;

            if (targetPlayer.Ship == ShipType.Spec)
                return;

            int specCount = 0;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player playerToCheck in _playerData.Players)
                    {
                        if (!playerToCheck.TryGetExtraData(_pdKey, out PlayerData? pd))
                            continue;

                        if (pd.Speccing == targetPlayer
                            && (_capabilityManager is null 
                                || !_capabilityManager.HasCapability(playerToCheck, Constants.Capabilities.InvisibleSpectator)
                                || _capabilityManager.HigherThan(player, playerToCheck)))
                        {
                            specCount++;

                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(playerToCheck.Name);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (specCount > 1)
                {
                    _chat.SendMessage(player, $"{specCount} players spectating {(player == targetPlayer ? "you" : targetPlayer.Name)}:");
                    _chat.SendWrappedText(player, sb);
                }
                else if (specCount == 1)
                {
                    _chat.SendMessage(player, $"1 player spectating {(player == targetPlayer ? "you" : targetPlayer.Name)}: {sb}");
                }
                else
                {
                    _chat.SendMessage(player, $"No players are spectating {(player == targetPlayer ? "you" : targetPlayer.Name)}.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena | CommandTarget.Player,
            Args = "[-t] [-n] [-s]",
            Description = """
                If sent as a priv message, turns energy viewing on for that player.
                If sent as a pub message, turns energy viewing on for the whole arena
                (note that this will only affect new players entering the arena).
                If -t is given, turns energy viewing on for teammates only.
                If -n is given, turns energy viewing off.
                If -s is given, turns energy viewing on/off for spectator mode.
                """)]
        private void Command_energy(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            target.TryGetPlayerTarget(out Player? targetPlayer);

            SeeEnergy newValue = SeeEnergy.All;
            bool spec = false;

            if (!parameters.IsEmpty && parameters.Contains("-t", StringComparison.OrdinalIgnoreCase))
                newValue = SeeEnergy.Team;

            if (!parameters.IsEmpty && parameters.Contains("-n", StringComparison.OrdinalIgnoreCase))
                newValue = SeeEnergy.None;

            if (!parameters.IsEmpty && parameters.Contains("-s", StringComparison.OrdinalIgnoreCase))
                spec = true;

            if (targetPlayer is not null)
            {
                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                if (spec)
                    pd.SeeNrgSpec = newValue;
                else
                    pd.SeeNrg = newValue;
            }
            else
            {
                if (player.Arena is null || !player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
                    return;

                if (spec)
                    ad.SpecSeeEnergy = newValue;
                else
                    ad.SpecSeeEnergy = newValue;
            }
        }

        private void DoSpawnCallback(Player player, SpawnCallback.SpawnReason reason)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            _mainloop.QueueMainWorkItem(
                MainloopWork_FireSpawnCallback,
                new SpawnDTO()
                {
                    Arena = arena,
                    Player = player,
                    Reason = reason,
                });

            static void MainloopWork_FireSpawnCallback(SpawnDTO dto)
            {
                if (dto.Arena == dto.Player.Arena)
                {
                    SpawnCallback.Fire(dto.Arena, dto.Player, dto.Reason);
                }
            }
        }

        private void DoShipFreqChangeCallback(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            _mainloop.QueueMainWorkItem(
                MainloopWork_FireShipFreqChangeCallback,
                new ShipFreqChangeDTO()
                {
                    Arena = arena,
                    Player = player,
                    NewShip = newShip,
                    OldShip = oldShip,
                    NewFreq = newFreq,
                    OldFreq = oldFreq,
                });

            static void MainloopWork_FireShipFreqChangeCallback(ShipFreqChangeDTO dto)
            {
                if (dto.Arena == dto.Player.Arena)
                {
                    ShipFreqChangeCallback.Fire(dto.Arena, dto.Player, dto.NewShip, dto.OldShip, dto.NewFreq, dto.OldFreq);
                }
            }
        }

        #region Helper types

        private struct AttachDTO
        {
            public required Arena Arena;
            public required Player Player;
            public required Player? To;
        }

        private struct SpawnDTO
        {
            public required Arena Arena;
            public required Player Player;
            public required SpawnCallback.SpawnReason Reason;
        }

        private struct ShipFreqChangeDTO
        {
            public required Arena Arena;
            public required Player Player;
            public required ShipType NewShip;
            public required ShipType OldShip;
            public required short NewFreq;
            public required short OldFreq;
        }

        [Flags]
        private enum PersonalGreen
        {
            None = 0,
            Thor = 1,
            Burst = 2,
            Brick = 4,
        }

        [Flags]
        private enum CheckFastBombing
        {
            None = 0,

            /// <summary>
            /// Send sysop alert when fastbombing is detected
            /// </summary>
            Alert = 1,

            /// <summary>
            /// Filter out fastbombs
            /// </summary>
            Filter = 2,

            /// <summary>
            /// Kick fastbombing player off
            /// </summary>
            Kick = 4,
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The player's latest position packet.
            /// </summary>
            public C2S_PositionPacket Position = new();

            /// <summary>
            /// Who the player is spectating, <see langword="null"/> means not spectating.
            /// </summary>
            /// <remarks>
            /// It is possible that this is not <see langword="null"/>, but the referenced player is in spec.
            /// This can happen when the last player in a ship changes to spectator mode.
            /// Anyone that was spectating that player is technically still spectating that player.
            /// If that player enters a ship, those that were previously spectating will continue to do so, without sending a <see cref="Packets.Game.C2SPacketType.SpecRequest"/> packet.
            /// To keep our state in sync with what clients think, this is not cleared when a player being spectated enters spec.
            /// </remarks>
            public Player? Speccing;

            /// <summary>
            /// Used for determining which weapon packets to ignore for the player, if any.
            /// e.g. if the player is lagging badly, it can be set to handicap against the player.
            /// </summary>
            public int IgnoreWeapons;

            /// <summary>
            /// # of deaths the player has had without firing, used to check if the player should be sent to spec
            /// </summary>
            public int DeathWithoutFiringCount;

            /// <summary>
            /// The # of players that are spectating the player that can see extra position data.
            /// </summary>
            public int EpdPlayerWatchCount;

            /// <summary>
            /// The # of module watches for extra position data on the player.
            /// </summary>
            public int EpdModuleWatchCount;

            /// <summary>
            /// Gets the total # of extra position data watches on the player.
            /// </summary>
            public int EpdWatchCount => EpdPlayerWatchCount + EpdModuleWatchCount;

            /// <summary>
            /// Whose energy levels the player can see.
            /// </summary>
            public SeeEnergy SeeNrg;

            /// <summary>
            /// Whose energy levels the player can see when in spectator mode.
            /// </summary>
            public SeeEnergy SeeNrgSpec;

            /// <summary>
            /// Whether the player can see extra position data.
            /// </summary>
            public bool SeeEpd;

            /// <summary>
            /// Whether the player is locked to their current ship/freq (excluding spec, one can always switch to spec).
            /// </summary>
            public bool LockShip;

            /// <summary>
            /// When the lock expires, or <see langword="null"/> for session-long lock.
            /// </summary>
            public DateTime? Expires;

            /// <summary>
            /// Whether the player is in a <see cref="MapRegion"/> that does not allow anti-warp.
            /// </summary>
            public bool MapRegionNoAnti;

            /// <summary>
            /// Whether the player is in a <see cref="MapRegion"/> that does not allow firing of weapons.
            /// </summary>
            public bool MapRegionNoWeapons;

            /// <summary>
            /// When we last updated the region-based flags.
            /// </summary>
            public DateTime LastRegionCheck;

            /// <summary>
            /// Set of regions the player was in during the last region check.
            /// </summary>
            public ImmutableHashSet<MapRegion> LastRegionSet = [];

            /// <summary>
            /// The ship of the player when their last position packet was processed.
            /// </summary>
            /// <remarks>
            /// Used to help determine whether to send a <see cref="WarpCallback"/>.
            /// </remarks>
            public ShipType? LastPositionPacketShip;

            /// <summary>
            /// When the player last shot a bomb, mine, or thor.
            /// Used to check for fast bombing.
            /// </summary>
            public ServerTick? LastBomb;

            /// <summary>
            /// When the a position packet was last sent containing the player's bounty.
            /// </summary>
            public ServerTick? BountyLastSent;

            public bool TryReset()
            {
                Position = new();
                Speccing = null;
                IgnoreWeapons = 0;
                DeathWithoutFiringCount = 0;
                EpdPlayerWatchCount = 0;
                EpdModuleWatchCount = 0;
                SeeNrg = SeeEnergy.None;
                SeeNrgSpec = SeeEnergy.None;
                SeeEpd = false;
                LockShip = false;
                Expires = null;
                MapRegionNoAnti = false;
                MapRegionNoWeapons = false;
                LastRegionCheck = default;
                LastRegionSet = [];
                LastPositionPacketShip = null;
                LastBomb = null;
                BountyLastSent = null;
                return true;
            }
        }

        private class ArenaData : IResettable
        {
            /// <summary>
            /// Client setting to multiply kill points if the killer was carrying a flag.
            /// </summary>
            public int FlaggerKillMultiplier;

            /// <summary>
            /// Whether spectators in the arena can see extra data for the person they're spectating.
            /// </summary>
            public bool SpecSeeExtra;

            /// <summary>
            /// Whose energy levels spectators can see.
            /// </summary>
            public SeeEnergy SpecSeeEnergy;

            /// <summary>
            /// Whose energy levels everyone can see.
            /// </summary>
            public SeeEnergy AllSeeEnergy;

            /// <summary>
            /// Which types of greens should not be shared with teammates.
            /// </summary>
            public PersonalGreen PersonalGreen;

            /// <summary>
            /// Whether the arena is locked (players not allowed to change ship/freq).
            /// </summary>
            public bool InitLockShip;

            /// <summary>
            /// Whether players that enter the arena are moved to spectator mode and the spectator freq.
            /// </summary>
            public bool InitSpec;

            /// <summary>
            /// The maximum # of times a player can die without firing a weapon before they are forced to spectator mode.
            /// </summary>
            public int MaxDeathWithoutFiring;

            /// <summary>
            /// How often to check for region enter/exit events (in ticks).
            /// </summary>
            public TimeSpan RegionCheckTime;

            /// <summary>
            /// Whether anti-warp is disabled for players in safe zones.
            /// </summary>
            public bool NoSafeAntiWarp;

            /// <summary>
            /// The amount of change in a players position (in pixels) that is considered a warp (only while he is flashing).
            /// </summary>
            public int WarpThresholdDelta;

            /// <summary>
            /// Whether to check for fast bombing and if so, which the action(s) to perform.
            /// </summary>
            public CheckFastBombing CheckFastBombing;

            /// <summary>
            /// Tuning for fast bomb detection.
            /// </summary>
            /// <remarks>
            /// A bomb/mine/thor is considered to be fast bombing if delay between 2 bombs/mines/thors is less than <ship>:BombFireDelay - Misc:FastBombingThreshold.
            /// </remarks>
            public short FastBombingThreshold;

            /// <summary>
            /// How far away to to send positions of players on radar (in pixels).
            /// </summary>
            public int PositionPixels;

            /// <summary>
            /// Percent of position packets with anti-warp enabled to send to the whole arena.
            /// </summary>
            public int SendAnti;

            /// <summary>
            /// Distance anti-warp affects other players (in pixels).
            /// </summary>
            public int AntiWarpRange;

            /// <summary>
            /// How long after a player dies before he can re-enter the game (in ticks).
            /// </summary>
            public int EnterDelay;

            /// <summary>
            /// How far away to send weapon packets (in pixels).
            /// </summary>
            /// <remarks>
            /// <see cref="WeaponData.Type"/> is represented by 5 bits, so max of 32 values.
            /// </remarks>
            public readonly int[] WeaponRange = new int[32];

            public bool TryReset()
            {
                FlaggerKillMultiplier = 0;
                SpecSeeExtra = false;
                SpecSeeEnergy = SeeEnergy.None;
                AllSeeEnergy = SeeEnergy.None;
                PersonalGreen = PersonalGreen.None;
                InitLockShip = false;
                InitSpec = false;
                MaxDeathWithoutFiring = 0;
                RegionCheckTime = default;
                NoSafeAntiWarp = false;
                WarpThresholdDelta = 0;
                CheckFastBombing = CheckFastBombing.None;
                FastBombingThreshold = 0;
                PositionPixels = 0;
                SendAnti = 0;
                AntiWarpRange = 0;
                EnterDelay = 0;
                Array.Clear(WeaponRange);
                return true;
            }
        }

        #endregion
    }
}
