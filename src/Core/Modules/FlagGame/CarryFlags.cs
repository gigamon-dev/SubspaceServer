using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SS.Core.Modules.FlagGame
{
    /// <summary>
    /// Module that manages flag games where flags can be carried.
    /// E.g. jackpot zone, running zone, warzone ctf.
    /// </summary>
    public class CarryFlags : IModule, ICarryFlagGame
    {
        public const int MaxFlags = 256; // continuum supports 303

        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private IMainloopTimer _mainloopTimer;
        private INetwork _network;
        private IPrng _prng;

        private InterfaceRegistrationToken<ICarryFlagGame> _carryFlagGameRegistrationToken;

        private static readonly NonTransientObjectPool<FlagInfo> _flagInfoPool = new(new FlagInfoPooledObjectPolicy());

        private ArenaDataKey<ArenaData> _adKey;

        private DefaultCarryFlagBehavior _defaultCarryFlagBehavior;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            INetwork network,
            IPrng prng)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _defaultCarryFlagBehavior = new(this, _logManager, _mapData, _prng);

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _network.AddPacket(C2SPacketType.TouchFlag, Packet_TouchFlag);
            _network.AddPacket(C2SPacketType.DropFlags, Packet_DropFlags);

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            NewPlayerCallback.Register(broker, Callback_NewPlayer);
            ShipFreqChangeCallback.Register(broker, Callback_ShipFreqChange);

            _carryFlagGameRegistrationToken = broker.RegisterInterface<ICarryFlagGame>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _carryFlagGameRegistrationToken);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);
            ShipFreqChangeCallback.Unregister(broker, Callback_ShipFreqChange);

            _network.RemovePacket(C2SPacketType.TouchFlag, Packet_TouchFlag);
            _network.RemovePacket(C2SPacketType.DropFlags, Packet_DropFlags);

            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region IFlagGame, ICarryFlagGame

        void IFlagGame.ResetGame(Arena arena)
        {
            ((ICarryFlagGame)this).ResetGame(arena, -1, 0);
        }

        int IFlagGame.GetFlagCount(Arena arena)
        {
            if (arena == null)
                return 0;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.GameState != GameState.Running)
                return 0;

            return ad.Flags.Count;
        }

        int IFlagGame.GetFlagCount(Arena arena, int freq)
        {
            if (arena == null)
                return 0;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.GameState != GameState.Running)
                return 0;

            int count = 0;
            foreach (FlagInfo flagInfo in ad.Flags)
            {
                if (flagInfo.Freq == freq)
                    count++;
            }

            return count;
        }

        ICarryFlagSettings ICarryFlagGame.GetSettings(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return null;

            return ad.Settings;
        }

        bool ICarryFlagGame.StartGame(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState == GameState.Stopped)
            {
                ad.GameState = GameState.Starting;
                ad.StartTimestamp = DateTime.UtcNow + ad.Settings.ResetDelay;

                // The flag spawn timer will place the flags.

                return true;
            }

            return false;
        }

        bool ICarryFlagGame.ResetGame(Arena arena, short winnerFreq, int points)
        {
            return ResetGame(arena, winnerFreq, points, true);
        }

        int ICarryFlagGame.GetFlagCount(Player player)
        {
            if (player == null)
                return 0;

            Arena arena = player.Arena;
            if (arena == null)
                return 0;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            if (ad.GameState != GameState.Running)
                return 0;

            int count = 0;
            foreach (FlagInfo flagInfo in ad.Flags)
            {
                if (flagInfo.Carrier == player)
                    count++;
            }

            return count;
        }

        short ICarryFlagGame.TransferFlagsForPlayerKill(Arena arena, Player killed, Player killer)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || ad.CarryFlagBehavior == null)
            {
                return 0;
            }

            short carried = killed.Packet.FlagsCarried;
            if (carried == 0)
                return 0;

            Span<short> flagIds = stackalloc short[carried];
            int flagCount = 0;

            for (short flagId = 0; flagId < ad.Flags.Count && flagCount < carried; flagId++)
            {
                FlagInfo flagInfo = ad.Flags[flagId]; // TODO: call ICarryFlagGame.TryGetFlagInfo()
                if (flagInfo.State == FlagState.Carried
                    && flagInfo.Carrier == killed)
                {
                    flagIds[flagCount++] = flagId;

                    killed.Packet.FlagsCarried--;

                    flagInfo.State = FlagState.None;
                    flagInfo.Carrier = null;

                    FlagLostCallback.Fire(arena, arena, killed, flagId, FlagLostReason.Killed);
                }
            }

            Debug.Assert(killed.Packet.FlagsCarried == 0);

            return ad.CarryFlagBehavior.PlayerKill(arena, killed, killer, flagIds);
        }

        bool ICarryFlagGame.TryAddFlag(Arena arena, out short flagId)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || ad.Flags.Count == MaxFlags)
            {
                flagId = default;
                return false;
            }

            FlagInfo flagInfo = _flagInfoPool.Get();
            ad.Flags.Add(flagInfo);
            flagId = (short)(ad.Flags.Count - 1);
            return true;
        }

        bool ICarryFlagGame.TryGetFlagInfo(Arena arena, short flagId, out IFlagInfo flagInfo)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || flagId >= ad.Flags.Count)
            {
                flagInfo = null;
                return false;
            }

            flagInfo = ad.Flags[flagId];
            return true;
        }

        bool ICarryFlagGame.TrySetFlagNeuted(Arena arena, short flagId, MapCoordinate? location, short freq)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || flagId >= ad.Flags.Count)
            {
                return false;
            }
            
            FlagInfo flagInfo = ad.Flags[flagId];
            if (flagInfo.State == FlagState.None)
            {
                // Already neuted. Keep track of location and freq as it could be used later (e.g. to spawn the flag).
                flagInfo.Location = location;
                flagInfo.Freq = freq;
                return true;
            }
            else if (flagInfo.State == FlagState.OnMap)
            {
                /*
                // hacky move of flag out of playable area
                // Removing a flag on the map. Fake it by moving it outside of the playable area.
                S2C_FlagLocation packet = new(flagId, -1, -1, -1);
                _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable);

                flagInfo.State = FlagState.None;
                flagInfo.Location = location;
                flagInfo.Freq = freq;

                _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Faked removing flag {flagId}.");

                return true;
                */
                return false;
            }
            else if (flagInfo.State == FlagState.Carried)
            {
                /*
                // hacky drop/re-pickup
                Player carrier = flagInfo.Carrier;

                S2C_FlagDrop flagDropPacket = new((short)carrier.Id);
                _network.SendToArena(arena, null, ref flagDropPacket, NetSendFlags.Reliable);

                carrier.Packet.FlagsCarried = 0;

                flagInfo.State = FlagState.None;
                flagInfo.Carrier = null;
                flagInfo.Location = location;
                flagInfo.Freq = freq;

                for (short otherFlagId = 0; otherFlagId < ad.Flags.Count; otherFlagId++)
                {
                    FlagInfo otherFlag = ad.Flags[otherFlagId];
                    if (otherFlag.State == FlagState.Carried
                        && otherFlag.Carrier == carrier)
                    {
                        S2C_FlagPickup flagPickupPacket = new(otherFlagId, (short)carrier.Id);
                        _network.SendToArena(arena, null, ref flagPickupPacket, NetSendFlags.Reliable);

                        carrier.Packet.FlagsCarried++;
                    }
                }

                if (carrier.Packet.FlagsCarried > 0)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Faked removing one carried flag {flagId}.");
                }

                // TODO: FlagLostCallback

                return true;
                */
                return false;
            }

            return false;
        }

        bool ICarryFlagGame.TrySetFlagOnMap(Arena arena, short flagId, MapCoordinate location, short freq)
        {
            if (arena == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || flagId >= ad.Flags.Count)
            {
                return false;
            }

            FlagInfo flagInfo = ad.Flags[flagId];
            if (flagInfo.State == FlagState.None)
            {
                S2C_FlagLocation flagLocationPacket = new(flagId, location.X, location.Y, freq);
                _network.SendToArena(arena, null, ref flagLocationPacket, NetSendFlags.Reliable);

                flagInfo.State = FlagState.OnMap;
                flagInfo.Location = location;
                flagInfo.Freq = freq;

                FlagOnMapCallback.Fire(arena, arena, flagId, location, freq);

                return true;
            }
            else if (flagInfo.State == FlagState.OnMap)
            {
                if (flagInfo.Location != location
                    || flagInfo.Freq != freq)
                {
                    S2C_FlagLocation flagLocationPacket = new(flagId, location.X, location.Y, freq);
                    _network.SendToArena(arena, null, ref flagLocationPacket, NetSendFlags.Reliable);

                    flagInfo.State = FlagState.OnMap;
                    flagInfo.Location = location;
                    flagInfo.Freq = freq;

                    FlagOnMapCallback.Fire(arena, arena, flagId, location, freq);
                }

                return true;
            }
            else if (flagInfo.State == FlagState.Carried)
            {
                // could do hacky drop/re-pickup

                return false;
            }

            return false;
        }

        bool ICarryFlagGame.TrySetFlagCarried(Arena arena, short flagId, Player carrier, FlagPickupReason reason)
        {
            if (arena == null
                || carrier == null
                || !arena.TryGetExtraData(_adKey, out ArenaData ad)
                || ad.GameState != GameState.Running
                || flagId >= ad.Flags.Count)
            {
                return false;
            }

            FlagInfo flagInfo = ad.Flags[flagId];
            if (flagInfo.State == FlagState.None)
            {
                if (reason != FlagPickupReason.Kill) // for kills, the S2C Kill packet is all that's needed
                {
                    S2C_FlagPickup flagPickupPacket = new(flagId, (short)carrier.Id);
                    _network.SendToArena(arena, null, ref flagPickupPacket, NetSendFlags.Reliable);
                }

                carrier.Packet.FlagsCarried++;

                flagInfo.State = FlagState.Carried;
                flagInfo.Carrier = carrier;
                flagInfo.Freq = carrier.Freq;
                flagInfo.Location = null;

                FlagGainCallback.Fire(arena, arena, carrier, flagId, reason);
            }
            else if (flagInfo.State == FlagState.OnMap)
            {
                if (reason != FlagPickupReason.Kill) // for kills, the S2C Kill packet is all that's needed
                {
                    S2C_FlagPickup flagPickupPacket = new(flagId, (short)carrier.Id);
                    _network.SendToArena(arena, null, ref flagPickupPacket, NetSendFlags.Reliable);
                }

                carrier.Packet.FlagsCarried++;

                flagInfo.State = FlagState.Carried;
                flagInfo.Carrier = carrier;
                flagInfo.Freq = carrier.Freq;
                flagInfo.Location = null;

                FlagGainCallback.Fire(arena, arena, carrier, flagId, reason);
            }
            else if (flagInfo.State == FlagState.Carried)
            {
                // could do hacky drop/re-pickup/pickup

                return false;
            }

            return false;
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

            if (ad.GameState != GameState.Running)
                return;

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
            short flagId = packet.FlagId;
            if (flagId < 0 || flagId >= ad.Flags.Count)
            {
                // Possible if a flag game was reset or # of flags was changed.
                _logManager.LogP(LogLevel.Info, nameof(StaticFlags), p, $"C2S_TouchFlag packet but bad flag id ({flagId}).");
                return;
            }

            FlagInfo flagInfo = ad.Flags[flagId];
            if (flagInfo.State != FlagState.OnMap)
            {
                // Possible if multiple players are racing to claim a flag.
                _logManager.LogP(LogLevel.Info, nameof(StaticFlags), p, $"C2S_TouchFlag packet but flag {flagId} was not on the map.");
                return;
            }

            ad.CarryFlagBehavior.TouchFlag(arena, p, flagId);
        }

        private void Packet_DropFlags(Player p, byte[] data, int length)
        {
            if (p == null)
                return;

            if (length != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(CarryFlags), p, $"Invalid drop flag packet length ({length}).");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Info, nameof(CarryFlags), p, $"Flag drop from bad state/arena.");
                return;
            }

            if (p.Ship == ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Info, nameof(CarryFlags), p, $"State sync problem: drop flag from spec.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (p.Packet.FlagsCarried == 0)
                return;

            S2C_FlagDrop flagDropPacket = new((short)p.Id);
            _network.SendToArena(arena, null, ref flagDropPacket, NetSendFlags.Reliable);

            AdjustCarriedFlags(
                arena,
                p,
                ((p.Position.Status & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone) ? AdjustFlagReason.InSafe : AdjustFlagReason.Dropped);
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                Settings config = new(_configManager, arena.Cfg);

                ConfigCarryFlags carryFlags = _configManager.GetEnum(arena.Cfg, "Flag", "CarryFlags", (ConfigCarryFlags)(-1));
                if (carryFlags >= ConfigCarryFlags.Yes && config.MinFlags > 0)
                {
                    ad.Settings = config;

                    ClearCarryFlagBehavior(arena, ad);
                    ad.CarryFlagBehavior = arena.GetInterface<ICarryFlagBehavior>() ?? _defaultCarryFlagBehavior;

                    ResetGame(arena, -1, 0, true);

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SpawnFlagTimer, arena);
                    _mainloopTimer.SetTimer(MainloopTimer_SpawnFlagTimer, 5000, 5000, arena, arena);

                    if (ad.FlagGameRegistrationToken == null)
                        ad.FlagGameRegistrationToken = arena.RegisterInterface<IFlagGame>(this);
                }
                else
                {
                    if (ad.GameState != GameState.Stopped)
                    {
                        ResetGame(arena, -1, 0, false);
                    }

                    ClearCarryFlagBehavior(arena, ad);

                    _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SpawnFlagTimer, arena);

                    if (ad.FlagGameRegistrationToken != null)
                        arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                _mainloopTimer.ClearTimer<Arena>(MainloopTimer_SpawnFlagTimer, arena);

                ClearCarryFlagBehavior(arena, ad);

                if (ad.FlagGameRegistrationToken != null)
                    arena.UnregisterInterface(ref ad.FlagGameRegistrationToken);
            }

            void ClearCarryFlagBehavior(Arena arena, ArenaData ad)
            {
                if (ad.CarryFlagBehavior != null)
                {
                    if (ad.CarryFlagBehavior != this)
                    {
                        arena.ReleaseInterface(ref ad.CarryFlagBehavior);
                    }
                    else
                    {
                        ad.CarryFlagBehavior = null;
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                    return;

                if (ad.GameState == GameState.Running)
                {
                    for (short flagId = 0; flagId < ad.Flags.Count; flagId++)
                    {
                        FlagInfo flagInfo = ad.Flags[flagId];
                        if (flagInfo.State == FlagState.OnMap)
                        {
                            S2C_FlagLocation locationPacket = new(
                                flagId, flagInfo.Location.Value.X, flagInfo.Location.Value.Y, flagInfo.Freq);

                            _network.SendToOne(player, ref locationPacket, NetSendFlags.Reliable);
                        }
                    }
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                // Cleanup any flags the player was carrying.
                AdjustCarriedFlags(arena, player, AdjustFlagReason.LeftArena);
            }
        }

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (player.Type == ClientType.Fake && player.Arena != null && !isNew)
            {
                // Cleanup any flags the player was carrying. For fake players, since PlayerAction.LeaveArena isn't called.
                AdjustCarriedFlags(player.Arena, player, AdjustFlagReason.LeftArena);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if(newShip == oldShip)
            {
                AdjustCarriedFlags(player.Arena, player, AdjustFlagReason.FreqChange);
            }
            else
            {
                AdjustCarriedFlags(player.Arena, player, AdjustFlagReason.ShipChange);
            }
        }

        #endregion

        private bool MainloopTimer_SpawnFlagTimer(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState == GameState.Starting
                && DateTime.UtcNow >= ad.StartTimestamp)
            {
                ad.GameState = GameState.Running;
                ad.CarryFlagBehavior.StartGame(arena);
            }
            //else if (ad.GameState == GameState.Running)
            //{
            //    for (short flagId = 0; flagId < ad.Flags.Count; flagId++)
            //    {
            //        FlagInfo flagInfo = ad.Flags[flagId];
            //        if (flagInfo.State == FlagState.None)
            //        {

            //        }
            //    }
            //}

            return true;
        }

        bool ResetGame(Arena arena, short winnerFreq, int points, bool allowAutoStart)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.GameState == GameState.Running)
            {
                S2C_FlagReset flagResetPacket = new(winnerFreq, points);
                _network.SendToArena(arena, null, ref flagResetPacket, NetSendFlags.Reliable);
            }

            for (int i = ad.Flags.Count - 1; i >= 0; i--)
            {
                FlagInfo flagInfo = ad.Flags[i];
                if (flagInfo.Carrier != null)
                {
                    flagInfo.Carrier.Packet.FlagsCarried--;
                }

                ad.Flags.RemoveAt(i);
                _flagInfoPool.Return(flagInfo);
            }

            ad.GameState = GameState.Stopped;

            FlagGameResetCallback.Fire(arena, arena, winnerFreq, points);

            bool started = false;

            if (allowAutoStart && ad.Settings.AutoStart)
            {
                started = ((ICarryFlagGame)this).StartGame(arena);
            }

            // TODO: ASSS has some logic that fires the SpawnCallback. Unsure why, but maybe S2C_FlagReset automatically resets the ships of the winner freq? Investigate when a scoring module is made.

            // ASSS ends the game interval here, but it probably is better to put that in a flag game scoring module.

            return started;
        }

        private void AdjustCarriedFlags(Arena arena, Player player, AdjustFlagReason reason)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            Span<short> flagIds = stackalloc short[ad.Flags.Count];
            int i = 0;
            for (short flagId = 0; flagId < ad.Flags.Count; flagId++)
            {
                FlagInfo flagInfo = ad.Flags[flagId];
                if (flagInfo.State == FlagState.Carried && flagInfo.Carrier == player)
                {
                    flagIds[i++] = flagId;
                    player.Packet.FlagsCarried--;
                    flagInfo.State = FlagState.None;
                    flagInfo.Carrier = null;

                    FlagLostCallback.Fire(arena, arena, player, flagId, reason.ToFlagLostReason());
                }
            }

            Debug.Assert(0 == player.Packet.FlagsCarried);

            if (i > 0)
            {
                ad.CarryFlagBehavior.AdjustFlags(arena, flagIds[..i], reason, player, ad.Flags[flagIds[0]].Freq);
            }
        }

        #region Helper types

        private class Settings : ICarryFlagSettings
        {
            public bool AutoStart { get; private set; }
            public TimeSpan ResetDelay { get; private set; }
            public MapCoordinate SpawnCoordinate { get; private set; }
            public int SpawnRadius { get; private set; }
            public int DropRadius { get; private set; }
            public bool FriendlyTransfer { get; private set; }
            public ConfigCarryFlags CarryFlags { get; private set; }
            public bool DropOwned { get; private set; }
            public bool DropCenter { get; private set; }
            public bool NeutOwned { get; private set; }
            public bool NeutCenter { get; private set; }
            public bool TeamKillOwned { get; private set; }
            public bool TeamKillCenter { get; private set; }
            public bool SafeOwned { get; private set; }
            public bool SafeCenter { get; private set; }
            public TimeSpan WinDelay { get; private set; }
            public int MaxFlags { get; private set; }
            public int MinFlags { get; private set; }

            [ConfigHelp("Flag", "AutoStart", ConfigScope.Arena, typeof(bool), DefaultValue = "1", 
                Description = "Whether a flag game will automatically start.")]
            [ConfigHelp("Flag", "SpawnX", ConfigScope.Arena, typeof(int), DefaultValue = "512",
                Description = "The X coordinate that new flags spawn at (in tiles).")]
            [ConfigHelp("Flag", "SpawnY", ConfigScope.Arena, typeof(int), DefaultValue = "512",
                Description = "The Y coordinate that new flags spawn at (in tiles).")]
            [ConfigHelp("Flag", "SpawnRadius", ConfigScope.Arena, typeof(int), DefaultValue = "50",
                Description = "How far from the spawn center that new flags spawn (in tiles).")]
            [ConfigHelp("Flag", "DropRadius", ConfigScope.Arena, typeof(int), DefaultValue = "2",
                Description = "How far from a player do dropped flags appear (in tiles).")]
            [ConfigHelp("Flag", "FriendlyTransfer", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether you get a teammates flags when you kill him.")]
            // Flag:CarryFlags is in ClientSettingsConfig
            [ConfigHelp("Flag", "DropOwned", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether flags you drop are owned by your team.")]
            [ConfigHelp("Flag", "DropCenter", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether flags dropped normally go in the center of the map, as opposed to near the player.")]
            [ConfigHelp("Flag", "NeutOwned", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether flags that are neut-dropped are owned by your team.")]
            [ConfigHelp("Flag", "NeutCenter", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether flags that are neut-dropped go in the center of the map, as opposed to near the player.")]
            [ConfigHelp("Flag", "TKOwned", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether flags that are dropped by a team-kill are owned by your team.")]
            [ConfigHelp("Flag", "TKCenter", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether flags that are dropped by a team-kill go in the center of the map, as opposed to near the player.")]
            [ConfigHelp("Flag", "SafeOwned", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = "Whether flags that are dropped from a safe zone are owned by your team.")]
            [ConfigHelp("Flag", "SafeCenter", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether flags that are dropped from a safe zone go in the center of the map, as opposed to near the player.")]
            [ConfigHelp("Flag", "WinDelay", ConfigScope.Arena, typeof(int), DefaultValue = "200",
                Description = "The amount of time needed for the win condition to be satisfied to count as a win (ticks).")]
            [ConfigHelp("Flag", "FlagCount", ConfigScope.Arena, typeof(int), DefaultValue = "0", Range = "0-256",
                Description = "How many flags are present in this arena. This can be set to a range. For example, 6-8 to allow anywhere between 6 and and 8 flags to spawn.")]
            public Settings(IConfigManager configManager, ConfigHandle ch)
            {
                AutoStart = configManager.GetInt(ch, "Flag", "AutoStart", 1) != 0;

                ResetDelay = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "Flag", "ResetDelay", 0) * 10);
                if (ResetDelay < TimeSpan.Zero)
                    ResetDelay = TimeSpan.Zero;

                SpawnCoordinate = new(
                    (short)configManager.GetInt(ch, "Flag", "SpawnX", 512),
                    (short)configManager.GetInt(ch, "Flag", "SpawnY", 512));

                SpawnRadius = configManager.GetInt(ch, "Flag", "SpawnRadius", 50);
                DropRadius = configManager.GetInt(ch, "Flag", "DropRadius", 2);

                FriendlyTransfer = configManager.GetInt(ch, "Flag", "FriendlyTransfer", 1) != 0;

                CarryFlags = configManager.GetEnum(ch, "Flag", "CarryFlags", (ConfigCarryFlags)(-1));

                DropOwned = configManager.GetInt(ch, "Flag", "DropOwned", 1) != 0;
                DropCenter = configManager.GetInt(ch, "Flag", "DropCenter", 0) != 0;

                NeutOwned = configManager.GetInt(ch, "Flag", "NeutOwned", 0) != 0;
                NeutCenter = configManager.GetInt(ch, "Flag", "NeutCenter", 0) != 0;

                TeamKillOwned = configManager.GetInt(ch, "Flag", "TKOwned", 1) != 0;
                TeamKillCenter = configManager.GetInt(ch, "Flag", "TKCenter", 0) != 0;

                SafeOwned = configManager.GetInt(ch, "Flag", "SafeOwned", 1) != 0;
                SafeCenter = configManager.GetInt(ch, "Flag", "SafeCenter", 0) != 0;

                WinDelay = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "Flag", "WinDelay", 200) * 10);

                string flagCountStr = configManager.GetStr(ch, "Flag", "FlagCount") ?? string.Empty;
                string[] minMaxArray = flagCountStr.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (minMaxArray.Length != 2
                    || !int.TryParse(minMaxArray[0], out int minFlags)
                    || !int.TryParse(minMaxArray[1], out int maxFlags))
                {
                    if (int.TryParse(flagCountStr, out int flagCount))
                    {
                        MinFlags = MaxFlags = flagCount;
                    }
                    else
                    {
                        MinFlags = MaxFlags = 0;
                    }
                }
                else
                {
                    MinFlags = minFlags;
                    MaxFlags = maxFlags;
                }

                MaxFlags = Math.Clamp(MaxFlags, 0, FlagGame.CarryFlags.MaxFlags);
                MinFlags = Math.Clamp(MinFlags, 0, MaxFlags);

                if (MinFlags == 0)
                    MaxFlags = 0;
            }
        }

        private enum GameState
        {
            /// <summary>
            /// The game is stopped. Flags will not be spawned.
            /// </summary>
            Stopped,

            /// <summary>
            /// The game is starting. Flags have not been initially spawned yet.
            /// </summary>
            Starting,

            /// <summary>
            /// The game is running. Flags have been initially spawned, but may need to be respawned.
            /// </summary>
            Running,
        }

        private class FlagInfo : IFlagInfo
        {
            public FlagState State { get; set; }
            public Player Carrier { get; set; }
            public MapCoordinate? Location { get; set; }
            public short Freq { get; set; }
        }

        private class ArenaData
        {
            public InterfaceRegistrationToken<IFlagGame> FlagGameRegistrationToken;

            public Settings Settings;
            public ICarryFlagBehavior CarryFlagBehavior;

            public readonly List<FlagInfo> Flags = new(MaxFlags);
            public GameState GameState = GameState.Stopped;
            public DateTime StartTimestamp;
        }

        private class FlagInfoPooledObjectPolicy : PooledObjectPolicy<FlagInfo>
        {
            public override FlagInfo Create()
            {
                return new FlagInfo()
                {
                    State = FlagState.None,
                    Carrier = null,
                    Location = null,
                    Freq = -1,
                };
            }

            public override bool Return(FlagInfo obj)
            {
                if (obj == null)
                    return false;

                obj.State = FlagState.None;
                obj.Carrier = null;
                obj.Location = null;
                obj.Freq = -1;

                return true;
            }
        }

        #endregion
    }
}
