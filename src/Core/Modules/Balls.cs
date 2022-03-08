using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides ball functionality. This includes:
    /// <list type="bullet">
    /// <item>Spawning/placing ball(s) on the map</item>
    /// <item>Sending ball location updates to players</item>
    /// <item>Player interactions with balls (pick up a ball, shoot a ball, score a goal with a ball)</item>
    /// </list>
    /// </summary>
    public class Balls : IModule, IBalls
    {
        /// <summary>
        /// Continuum supports up to a maximum of 8 balls.
        /// </summary>
        private const int MaxBalls = 8;
        private const NetSendFlags BallSendFlags = NetSendFlags.PriorityP4;

        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private IMapData _mapData;
        private INetwork _network;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken<IBalls> _iBallsToken;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            INetwork network,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            ShipFreqChangeCallback.Register(_broker, Callback_ShipFreqChange);
            KillCallback.Register(_broker, Callback_Kill);

            _network.AddPacket(C2SPacketType.PickupBall, Packet_PickupBall);
            _network.AddPacket(C2SPacketType.ShootBall, Packet_ShootBall);
            _network.AddPacket(C2SPacketType.Goal, Packet_Goal);

            _mainloopTimer.SetTimer(MainloopTimer_BasicBallTimer, 3000, 250, null);

            _iBallsToken = _broker.RegisterInterface<IBalls>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iBallsToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(MainloopTimer_BasicBallTimer, null);

            _network.RemovePacket(C2SPacketType.PickupBall, Packet_PickupBall);
            _network.RemovePacket(C2SPacketType.ShootBall, Packet_ShootBall);
            _network.RemovePacket(C2SPacketType.Goal, Packet_Goal);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            ShipFreqChangeCallback.Unregister(_broker, Callback_ShipFreqChange);
            KillCallback.Unregister(_broker, Callback_Kill);

            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region IBalls

        bool IBalls.TryGetBallSettings(Arena arena, out BallSettings ballSettings)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                ballSettings = default;
                return false;
            }

            lock (ad.Lock)
            {
                ballSettings = ad.Settings; // copy
                return true;
            }
        }

        bool IBalls.TrySetBallCount(Arena arena, int ballCount)
        {
            if (arena == null)
                return false;

            if (ballCount < 0 || ballCount > MaxBalls)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (TryChangeBallCount(arena, ballCount))
                {
                    ad.BallCountOverridden = true; // An outside module is changing the state.
                    return true;
                }

                return false;
            }
        }

        bool IBalls.TryGetBallData(Arena arena, int ballId, out BallData ballData)
        {
            if (arena == null
                || ballId < 0
                || !arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                ballData = default;
                return false;
            }

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount
                    || ballId >= ad.Balls.Length)
                {
                    ballData = default;
                    return false;
                }

                ballData = ad.Balls[ballId];
                return true;
            }
        }

        bool IBalls.TryPlaceBall(Arena arena, byte ballId, ref BallData ballData) => TryPlaceBall(arena, ballId, ref ballData);

        bool IBalls.TrySpawnBall(Arena arena, int ballId) => TrySpawnBall(arena, ballId);

        [ConfigHelp("Soccer", "NewGameDelay", ConfigScope.Arena, typeof(int), DefaultValue = "-3000",
            Description = "How long to wait between games (in ticks). If this is negative, the actual delay is random, betwen zero and the absolute value.")]
        void IBalls.EndGame(Arena arena)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                for (int i = 0; i < ad.BallCount; i++)
                {
                    PhaseBall(arena, i);
                    ad.Balls[i].State = BallState.Waiting;
                    ad.Balls[i].Carrier = null;
                }

                int newGameDelay = _configManager.GetInt(arena.Cfg, "Soccer", "NewGameDelay", -3000);
                if (newGameDelay < 0)
                    newGameDelay = _prng.Number(0, -newGameDelay);

                for (int i = 0; i < ad.BallCount; i++)
                    ad.Balls[i].Time = ServerTick.Now + (uint)newGameDelay;
            }

            IPersistExecutor persistExecutor = _broker.GetInterface<IPersistExecutor>();
            if (persistExecutor != null)
            {
                try
                {
                    persistExecutor.EndInterval(PersistInterval.Game, arena);
                }
                finally
                {
                    _broker.ReleaseInterface(ref persistExecutor);
                }
            }
        }

        void IBalls.GetGoalInfo(Arena arena, int freq, MapCoordinate coordinate, out bool isScorable, out bool isGoalOwner) => GetGoalInfo(arena, freq, coordinate, out isScorable, out isGoalOwner);

        #endregion

        #region Callback handlers

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                if (action == ArenaAction.Create)
                {
                    ad.LastSendTime = ServerTick.Now;
                    ad.BallCountOverridden = false;
                    ad.BallCount = LoadBallSettings(arena);

                    for (int i = 0; i < MaxBalls; i++)
                    {
                        if (i < ad.BallCount)
                        {
                            TrySpawnBall(arena, i);
                        }
                        else
                        {
                            InitBall(arena, i);
                        }

                        ad.Previous[i] = ad.Balls[i];
                    }

                    if (ad.BallCount > 0)
                        _logManager.LogA(LogLevel.Drivel, nameof(Balls), arena, $"{ad.BallCount} balls spawned.");
                }
                else if (action == ArenaAction.Destroy)
                {
                    // clean up ball data
                    ad.BallCount = 0;

                    // clean up internal data
                    ad.BallCountOverridden = false;
                }
                else if (action == ArenaAction.ConfChanged)
                {
                    int oldBallCount = ad.BallCount;
                    int newBallCount = LoadBallSettings(arena);

                    // If the ball count changed but it wasn't changed by a module or command, allow the new setting to change the ball count.
                    if (newBallCount != oldBallCount && !ad.BallCountOverridden)
                    {
                        TryChangeBallCount(arena, newBallCount);
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            // Players entering will automaticaly get ball information by packets sent by the timer. Nothing special needed here for that.

            if (action == PlayerAction.LeaveArena)
                CleanupAfter(arena, p, null, true, true);
        }

        private void Callback_ShipFreqChange(Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            CleanupAfter(p.Arena, p, null, true, false);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                CleanupAfter(arena, killed, killer, !ad.Settings.DeathScoresGoal, false);
            }
        }

        #endregion

        #region Packet handlers

        private void Packet_PickupBall(Player p, byte[] data, int length)
        {
            if (length != C2S_PickupBall.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), p, $"Bad ball pick up packet (length={length}.");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, $"Ball pick up packet from bad arena or status (status={p.Status}).");
                return;
            }

            if (p.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: ball pick up from spectator mode.");
                return;
            }

            ref C2S_PickupBall c2s = ref MemoryMarshal.AsRef<C2S_PickupBall>(data);

            if (p.Flags.NoFlagsBalls)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Balls), p, $"Too lagged to pick up ball {c2s.BallId}.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                byte ballId = c2s.BallId;

                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, $"State sync problem: tried to pick up nonexistant ball {ballId}");
                    return;
                }

                ref BallData bd = ref ad.Balls[ballId];
                ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[ballId];

                // Make sure someone else didn't get it first.
                if (bd.State != BallState.OnMap)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: tried to pick up a ball that can't be picked up at the moment.");
                    return;
                }

                if (c2s.Time != bd.Time && (p != extraInfo.LastKiller || c2s.Time != extraInfo.KillerValidPickupTime))
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: tried to pick up a ball from stale coords.");
                    return;
                }

                // Make sure the player doesn't carry more than one ball.
                for (int i = 0; i < ad.BallCount; i++)
                {
                    if (ad.Balls[i].Carrier == p
                        && ad.Balls[i].State == BallState.Carried
                        && i != ballId)
                    {
                        return;
                    }
                }

                BallData defaultBallData = bd;

                bd.State = BallState.Carried;
                bd.X = p.Position.X;
                bd.Y = p.Position.Y;
                bd.XSpeed = 0;
                bd.YSpeed = 0;
                bd.Carrier = p;
                bd.Freq = p.Freq;
                bd.Time = 0;
                bd.LastUpdate = ServerTick.Now;

                bool allow = true;

                // Consult advisors to allow other modules to affect the pickup.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (!(allow = advisor.AllowPickupBall(arena, p, ballId, ref bd)))
                        break;
                }

                if (!allow)
                {
                    bd = defaultBallData;
                    SendBallPacket(arena, ballId);
                }
                else
                {
                    ad.Previous[ballId] = defaultBallData;
                    extraInfo.LastKiller = null;
                    SendBallPacket(arena, ballId);

                    // now call callbacks
                    BallPickupCallback.Fire(arena, arena, p, ballId);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), p, $"Picked up ball {ballId}.");
                }
            }
        }

        private void Packet_ShootBall(Player p, byte[] data, int length)
        {
            if (length != BallPacket.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), p, $"Bad ball shoot packet (length={length}).");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "Ball fire packet from bad arena or status.");
                return;
            }

            if (p.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: ball shoot packet from specator mode.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            ref BallPacket c2s = ref MemoryMarshal.AsRef<BallPacket>(data);
            byte ballId = c2s.BallId;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, $"State sync problem: tried to shoot nonexistant ball {ballId}");
                    return;
                }

                ref BallData bd = ref ad.Balls[ballId];
                ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[ballId];

                if (bd.State != BallState.Carried || bd.Carrier != p)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: tried to shoot ball he wasn't carrying.");
                    return;
                }

                BallData defaultBallData = bd;

                bd.State = BallState.OnMap;
                bd.X = c2s.X;
                bd.Y = c2s.Y;
                bd.XSpeed = c2s.XSpeed;
                bd.YSpeed = c2s.YSpeed;
                bd.Freq = p.Freq;
                bd.Time = c2s.Time;
                bd.LastUpdate = ServerTick.Now;

                bool allow = true;

                // Consult advisors to allow other modules to affect the ball shot.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (!(allow = advisor.AllowShootBall(arena, p, ballId, false, ref bd)))
                        break;
                }

                if (!allow)
                {
                    bd = defaultBallData;
                    SendBallPacket(arena, ballId);
                }
                else
                {
                    ad.Previous[ballId] = defaultBallData;
                    extraInfo.LastKiller = null;
                    SendBallPacket(arena, ballId);

                    // now call callbacks
                    BallShootCallback.Fire(arena, arena, p, ballId);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), p, $"Shot ball {ballId}.");

                    MapCoordinate mapCoordinate = new((short)(bd.X / 16), (short)(bd.Y / 16));
                    if (bd.Carrier != null
                        && _mapData.GetTile(arena, mapCoordinate)?.IsGoal == true)
                    {
                        // Shot a ball on top of a goal tile.
                        // Check whether it's a goal and if it is don't wait for the goal packet.
                        // Waiting for the goal packet is undesirable because it is a race between who has the better connection.
                        _logManager.LogP(LogLevel.Drivel, nameof(Balls), p, $"Shot ball {ballId} on top of goal tile.");
                        HandleGoal(arena, bd.Carrier, ballId, mapCoordinate);
                    }
                }
            }
        }

        private void Packet_Goal(Player p, byte[] data, int length)
        {
            if (length != C2S_Goal.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), p, $"Bad ball goal packet (length={length}).");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "Ball goal packet from bad arena or status.");
                return;
            }

            if (p.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: ball goal packet from specator mode.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            ref C2S_Goal c2s = ref MemoryMarshal.AsRef<C2S_Goal>(data);
            int ballId = c2s.BallId;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, $"State sync problem: tried a goal for nonexistant ball {ballId}");
                    return;
                }

                ref BallData bd = ref ad.Balls[ballId];

                // We use this as a way to check for duplicated goals.
                if (bd.Carrier == null)
                {
                    return;
                }

                if (bd.State != BallState.OnMap)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: sent goal for carried ball.");
                    return;
                }

                if (p != bd.Carrier)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), p, "State sync problem: sent goal for ball he didn't shoot.");
                    return;
                }

                HandleGoal(arena, p, ballId, new MapCoordinate(c2s.X, c2s.Y));
            }
        }

        #endregion

        private bool MainloopTimer_BasicBallTimer()
        {
            _arenaManager.Lock();

            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (arena.Status != ArenaState.Running)
                        continue;

                    if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                        continue;

                    lock (ad.Lock)
                    {
                        if (ad.BallCount <= 0)
                            continue;

                        // See if we're ready to send packets.
                        ServerTick now = ServerTick.Now;

                        if (now - ad.LastSendTime < ad.Settings.SendTime)
                            continue;

                        for (int ballId = 0; ballId < ad.BallCount; ballId++)
                        {
                            ref BallData b = ref ad.Balls[ballId];

                            if (b.State == BallState.OnMap)
                            {
                                // It's on the map, just send the position update.
                                SendBallPacket(arena, ballId);
                            }
                            else if (b.State == BallState.Carried)
                            {
                                // It's being carried, update its x,y coords.
                                var position = b.Carrier.Position;
                                b.X = position.X;
                                b.Y = position.Y;

                                SendBallPacket(arena, ballId);
                            }
                            else if (b.State == BallState.Waiting)
                            {
                                if (now >= b.Time)
                                {
                                    TrySpawnBall(arena, ballId);
                                }
                            }
                        }

                        ad.LastSendTime = now;
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            return true;
        }

        [ConfigHelp("Soccer", "BallCount", ConfigScope.Arena, typeof(int), DefaultValue = "0", Range = "0-8",
            Description = "The number of balls in this arena.")]
        // Note: Soccer:Mode is a client setting. So, it's [ConfigHelp] is in ClientSettingsConfig.cs
        [ConfigHelp("Soccer", "SpawnX[N]", ConfigScope.Arena, typeof(int), 
            Description = "The X coordinate that the ball spawns at (in tiles). " +
            "N is omitted from the first setting. N can be from 1 to 7 for subsequent settings. " +
            "If there are more balls than spawn settings defined, later balls will repeat the spawns in order. " +
            "For example, with 3 spawns, the fourth ball uses the first spawn, the fifth ball uses the second. " +
            "If only part of a spawn is undefined, that part will default to the first spawn's setting.")]
        [ConfigHelp("Soccer", "SpawnY[N]", ConfigScope.Arena, typeof(int), 
            Description = "The Y coordinate that the ball spawns at (in tiles). " +
            "N is omitted from the first setting. N can be from 1 to 7 for subsequent settings. " +
            "If there are more balls than spawn settings defined, later balls will repeat the spawns in order. " +
            "For example, with 3 spawns, the fourth ball uses the first spawn, the fifth ball uses the second. " +
            "If only part of a spawn is undefined, that part will default to the first spawn's setting.")]
        [ConfigHelp("Soccer", "SpawnRadius[N]", ConfigScope.Arena, typeof(int), 
            Description = "How far from the spawn center the ball can spawn (in tiles). " +
            "N is omitted from the first setting. N can be from 1 to 7 for subsequent settings. " +
            "If there are more balls than spawn settings defined, later balls will repeat the spawns in order. " +
            "For example, with 3 spawns, the fourth ball uses the first spawn, the fifth ball uses the second. " +
            "If only part of a spawn is undefined, that part will default to the first spawn's setting.")]
        [ConfigHelp("Soccer", "SendTime", ConfigScope.Arena, typeof(int), DefaultValue = "100", Range = "25-500",
            Description = "How often the server sends ball positions (in ticks).")]
        [ConfigHelp("Soccer", "GoalDelay", ConfigScope.Arena, typeof(int), DefaultValue = "0", 
            Description = "How long after a goal before the ball appears (in ticks).")]
        [ConfigHelp("Soccer", "AllowGoalByDeath", ConfigScope.Arena, typeof(bool), DefaultValue = "0", 
            Description = "Whether a goal is scored if a player dies while carrying the ball on a goal tile.")]
        [ConfigHelp("Soccer", "KillerIgnorePassDelay", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "How much 'pass delay' should be trimmed off for someone killing a ball carrier.")]
        private int LoadBallSettings(Arena arena)
        {
            if (arena == null)
                return 0;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            int newSpawnCount = 1;

            lock (ad.Lock)
            {
                int ballCount = _configManager.GetInt(arena.Cfg, "Soccer", "BallCount", 0);

                if (ballCount < 0)
                    ballCount = 0;
                else if (ballCount > MaxBalls)
                    ballCount = MaxBalls;

                ad.Settings.Mode = _configManager.GetEnum(arena.Cfg, "Soccer", "Mode", SoccerMode.All);

                Span<BallSpawn> spawns = stackalloc BallSpawn[MaxBalls];

                spawns[0].X = _configManager.GetInt(arena.Cfg, "Soccer", "SpawnX", 512);
                spawns[0].Y = _configManager.GetInt(arena.Cfg, "Soccer", "SpawnY", 512);
                spawns[0].Radius = _configManager.GetInt(arena.Cfg, "Soccer", "SpawnRadius", 20);

                for (int i = 1; i < MaxBalls; i++)
                {
                    spawns[i].X = _configManager.GetInt(arena.Cfg, "Soccer", $"SpawnX{i}", -1);
                    spawns[i].Y = _configManager.GetInt(arena.Cfg, "Soccer", $"SpawnY{i}", -1);

                    if(spawns[i].X == -1  && spawns[i].Y == -1)
                    {
                        break;
                    }

                    newSpawnCount++;

                    if (spawns[i].X == -1)
                    {
                        spawns[i].X = spawns[0].X;
                    }
                    else if(spawns[i].Y == -1)
                    {
                        spawns[i].Y = spawns[0].Y;
                    }

                    spawns[i].Radius = _configManager.GetInt(arena.Cfg, "Soccer", $"SpawnRadius{i}", -1);

                    if (spawns[i].Radius == -1)
                    {
                        spawns[i].Radius = spawns[0].Radius;
                    }
                }

                if (ad.Spawns == null || ad.Spawns.Length != newSpawnCount)
                {
                    ad.Spawns = new BallSpawn[newSpawnCount];
                }

                spawns.Slice(0, newSpawnCount).CopyTo(ad.Spawns);

                ad.Settings.SendTime = _configManager.GetInt(arena.Cfg, "Soccer", "SendTime", 100);
                if (ad.Settings.SendTime < 25)
                    ad.Settings.SendTime = 25;
                else if (ad.Settings.SendTime > 500)
                    ad.Settings.SendTime = 500;

                ad.Settings.RespawnTimeAfterGoal = _configManager.GetInt(arena.Cfg, "Soccer", "GoalDelay", 0);
                ad.Settings.DeathScoresGoal = _configManager.GetInt(arena.Cfg, "Soccer", "AllowGoalByDeath", 0) != 0;
                ad.Settings.KillerIgnorePassDelay = _configManager.GetInt(arena.Cfg, "Soccer", "KillerIgnorePassDelay", 0);

                return ballCount;
            }
        }

        private bool TryChangeBallCount(Arena arena, int newBallCount)
        {
            if (arena == null)
                return false;

            if (newBallCount < 0 || newBallCount > MaxBalls)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                int oldBallCount = ad.BallCount;

                if (newBallCount < oldBallCount)
                {
                    // We have to remove some balls. There is no clean way to do this (as of now).
                    // What we do is "phase" the ball so it can't be picked up by clients and move it outside of the field of play.
                    // Players currently in the arena will still know about the ball, but it's out of reach.
                    // New players to the arena, will not know it existed.
                    for (int i = newBallCount; i < oldBallCount; i++)
                    {
                        PhaseBall(arena, i);
                    }
                }

                ad.BallCount = newBallCount;

                if (newBallCount > oldBallCount)
                {
                    for (int i = oldBallCount; i < newBallCount; i++)
                    {
                        TrySpawnBall(arena, i);
                    }
                }

                BallCountChangedCallback.Fire(arena, arena, newBallCount, oldBallCount);
            }

            return true;
        }

        private void InitBall(Arena arena, int ballId)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                ref BallData bd = ref ad.Balls[ballId];
                bd.State = BallState.OnMap;
                bd.X = bd.Y = 30000;
                bd.XSpeed = bd.YSpeed = 0;
                bd.Time = 0; // This is the key for making it phased.
                bd.LastUpdate = ServerTick.Now;
                bd.Carrier = null;
            }
        }

        private void PhaseBall(Arena arena, int ballId)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                InitBall(arena, ballId);
                SendBallPacket(arena, ballId);
            }
        }

        private bool TrySpawnBall(Arena arena, int ballId)
        {
            if (arena == null)
                return false;

            if (ballId < 0)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                    return false;

                BallData d;
                d.State = BallState.OnMap;
                d.XSpeed = d.YSpeed = 0;
                d.Carrier = null;
                d.Freq = -1;
                d.Time = ServerTick.Now;
                d.LastUpdate = 0;

                int idx;
                if (ballId < ad.Spawns.Length)
                {
                    // We have a defined spawn for this ball.
                    idx = ballId;
                }
                else
                {
                    // We don't have a specific spawn for this ball, borrow another one.
                    idx = ballId % ad.Spawns.Length;
                }

                int radius = ad.Spawns[idx].Radius;

                double randomRadius = _prng.Uniform() * radius;
                double randomAngle = _prng.Uniform() * Math.PI * 2d;
                int x = ad.Spawns[idx].X + (int)(randomRadius * Math.Cos(randomAngle));
                int y = ad.Spawns[idx].Y + (int)(randomRadius * Math.Sin(randomAngle));

                // wrap around, don't clip, so raddii of 2048 from a corner work properly
                while (x < 0) x += 1024;
                while (x > 1023) x -= 1024;
                while (y < 0) y += 1024;
                while (y > 1023) y -= 1024;

                // Ask map data to move it to the nearest empty tile.
                short sx = (short)x;
                short sy = (short)y;
                if (!_mapData.TryFindEmptyTileNear(arena, ref sx, ref sy))
                {
                    _logManager.LogA(LogLevel.Warn, nameof(Balls), arena, $"Unable to find empty tile to spawn ball at ({x},{y}).");
                    return false;
                }

                // Place it randomly within the chosen tile.
                radius = (int)_prng.Get32() & 0xff; // random 8 bits
                sx <<= 4;
                sy <<= 4;
                sx |= (short)(radius / 16); // 4 most significant bits of the 8
                sy |= (short)(radius % 16); // 4 least significant bits of the 8

                d.X = sx;
                d.Y = sy;

                TryPlaceBall(arena, ballId, ref d);
            }

            return true;
        }

        private bool TryPlaceBall(Arena arena, int ballId, ref BallData newPos)
        {
            if (arena == null)
                return false;

            if (ballId < 0)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            // Keep information consistent, player's freq for owned balls, freq of -1 for unowned balls.
            newPos.Freq = (newPos.Carrier != null) ? newPos.Carrier.Freq : -1;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                    return false;

                ad.Previous[ballId] = ad.Balls[ballId];
                ad.Balls[ballId] = newPos;
                ad.ExtraBallStateInfo[ballId].LastKiller = null;
                SendBallPacket(arena, ballId);
            }

            _logManager.LogA(LogLevel.Drivel, nameof(Balls), arena, $"Ball {ballId} is at ({newPos.X},{newPos.Y}).");
            return true;
        }

        private void SendBallPacket(Arena arena, int ballId)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                ref BallData bd = ref ad.Balls[ballId];
                ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[ballId];

                uint time;
                if (bd.State == BallState.Carried)
                    time = 0;
                else if (bd.State == BallState.OnMap)
                    time = bd.Time;
                else
                    return; // data is only sent for balls that are carried or on the map

                BallPacket bp = new(
                    true,
                    (byte)ballId,
                    bd.X,
                    bd.Y,
                    bd.XSpeed,
                    bd.YSpeed,
                    (short)(bd.Carrier?.Id ?? -1),
                    time);

                var bpBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref bp, 1));

                _network.SendToArena(arena, extraInfo.LastKiller, bpBytes, NetSendFlags.Unreliable | BallSendFlags);

                if (extraInfo.LastKiller != null && bd.State == BallState.OnMap)
                {
                    bp.Time = extraInfo.KillerValidPickupTime;
                    _network.SendToOne(extraInfo.LastKiller, bpBytes, NetSendFlags.Unreliable | BallSendFlags);
                }
            }
        }

        private void HandleGoal(Arena arena, Player p, int ballId, MapCoordinate mapCoordinate)
        {
            if (arena == null)
                return;

            if (p == null)
                return;

            if (ballId < 0)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                if (ballId > ad.BallCount)
                    return;

                ref BallData bd = ref ad.Balls[ballId];
                BallData newBallData = bd;

                bool block = false;

                // TODO: Move this into the GoalPoints module as an IBallsAdvisor. The GoalPoints module does not exist yet as of this writing.
                // Check that the goal is allowed (the tile is a goal and it can be scored on by the player's freq).
                GetGoalInfo(arena, p.Freq, mapCoordinate, out bool isScorable, out _);

                if (!isScorable)
                {
                    block = true;
                }

                // Consult advisors to allow other modules to affect the goal.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    bool allow = advisor.AllowGoal(arena, p, ballId, mapCoordinate, ref bd);
                    if (!allow)
                    {
                        block = true;

                        // At this point, we will allow other modules to redirect the goal's path but the goal being blocked is final.
                    }
                }

                if (block)
                {
                    // Update ball data and transmit it assuming it was changed.
                    // Barring extreme circumstances, using this check should not be a problem.
                    if (bd.LastUpdate != newBallData.LastUpdate)
                    {
                        ad.Previous[ballId] = bd;
                        bd = newBallData;
                        SendBallPacket(arena, ballId);
                    }
                }
                else
                {
                    ad.Previous[ballId] = bd;

                    // Send ball update.
                    if (bd.State != BallState.OnMap)
                    {
                        // Don't respawn the ball.
                    }
                    else if (ad.Settings.RespawnTimeAfterGoal == 0)
                    {
                        // No delay, spawn it now.
                        TrySpawnBall(arena, ballId);
                    }
                    else
                    {
                        // Phase it and set it to waiting.
                        ServerTick now = ServerTick.Now;
                        PhaseBall(arena, ballId);
                        bd.State = BallState.Waiting;
                        bd.Carrier = null;
                        bd.Time = now + (uint)ad.Settings.RespawnTimeAfterGoal;
                        bd.LastUpdate = now;
                    }

                    BallGoalCallback.Fire(arena, arena, p, (byte)ballId, mapCoordinate);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), p, $"Goal with ball {ballId} at ({mapCoordinate.X},{mapCoordinate.Y}).");
                }
            }
        }

        private void GetGoalInfo(Arena arena, int freq, MapCoordinate coordinate, out bool isScorable, out bool isGoalOwner)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (_mapData.GetTile(arena, coordinate)?.IsGoal != true)
            {
                isScorable = false;
                isGoalOwner = false;
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                isScorable = false;
                isGoalOwner = false;
                return;
            }

            SoccerMode mode;

            lock (ad.Lock)
            {
                mode = ad.Settings.Mode;
            }

            (short x, short y) = coordinate;
            
            switch (mode)
            {
                case SoccerMode.All:
                    isScorable = true; // any freq can score on it
                    isGoalOwner = false; // it's not any team's own goal
                    return;

                case SoccerMode.LeftRight:
                    freq %= 2;
                    isScorable = x < 512 ? freq == 1 : freq == 0;
                    isGoalOwner = !isScorable;
                    return;

                case SoccerMode.TopBottom:
                    freq %= 2;
                    isScorable = x < 512 ? freq == 1 : freq == 0;
                    isGoalOwner = !isScorable;
                    return;

                case SoccerMode.QuadrantsDefend1:
                    freq %= 4;
                    if (x < 512)
                    {
                        if (y < 512)
                        {
                            // top left
                            isGoalOwner = freq == 0;
                            isScorable = !isGoalOwner;
                        }
                        else
                        {
                            // bottom left
                            isGoalOwner = freq == 2;
                            isScorable = !isGoalOwner;
                        }
                    }
                    else
                    {
                        if (y < 512)
                        {
                            // top right
                            isGoalOwner = freq == 1;
                            isScorable = !isGoalOwner;
                        }
                        else
                        {
                            // bottom right
                            isGoalOwner = freq == 3;
                            isScorable = !isGoalOwner;
                        }
                    }
                    return;

                case SoccerMode.QuadrantsDefend3:
                    freq %= 4;
                    if (x < 512)
                    {
                        if (y < 512)
                        {
                            // top left
                            isScorable = freq == 3;
                        }
                        else
                        {
                            // bottom left
                            isScorable = freq == 1;
                        }
                    }
                    else
                    {
                        if (y < 512)
                        {
                            // top right
                            isScorable = freq == 2;
                        }
                        else
                        {
                            // bottom right
                            isScorable = freq == 0;
                        }
                    }
                    isGoalOwner = false; // no team is the sole owner
                    return;

                case SoccerMode.SidesDefend1:
                    freq %= 4;
                    int? ownerFreq = null;

                    if (x < y)
                    {
                        if (x < 1023 - y)
                        {
                            // left
                            ownerFreq = 0;
                        }
                        else
                        {
                            // bottom
                            ownerFreq = 1;
                        }
                    }
                    else if (x > y)
                    {
                        if (x < 1023 - y)
                        {
                            // top
                            ownerFreq = 2;
                        }
                        else
                        {
                            // right
                            ownerFreq = 3;
                        }
                    }

                    isGoalOwner = ownerFreq == freq;
                    isScorable = ownerFreq != null && ownerFreq != freq;
                    return;

                case SoccerMode.SidesDefend3:
                    freq %= 4;
                    int? scorableFreq = null;

                    if (x < y)
                    {
                        if (x < 1023 - y)
                        {
                            // left
                            scorableFreq = 3;
                        }
                        else
                        {
                            // bottom
                            scorableFreq = 2;
                        }
                    }
                    else if (x > y)
                    {
                        if (x < 1023 - y)
                        {
                            // top
                            scorableFreq = 1;
                        }
                        else
                        {
                            // right
                            scorableFreq = 3;
                        }
                    }

                    isScorable = scorableFreq == freq;
                    isGoalOwner = false; // no team is the sole owner
                    return;

                default:
                    throw new Exception("Invalid Soccer:Mode.");
            }
        }

        private void CleanupAfter(Arena arena, Player p, Player killer, bool newt, bool isLeaving)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            // Nake sure that if someone leaves, any balls the player was carrying drop.
            lock (ad.Lock)
            {
                for (int i = 0; i < ad.BallCount; i++)
                {
                    ref BallData b = ref ad.Balls[i];
                    ref BallData prev = ref ad.Previous[i];
                    ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[i];

                    if (extraInfo.LastKiller == p)
                    {
                        // This info no longer applies to this player.
                        extraInfo.LastKiller = null;
                    }

                    if (isLeaving && prev.Carrier == p)
                    {
                        // Prevent stale player from appearing in historical data.
                        prev.Carrier = null;
                    }

                    if (b.State == BallState.Carried && b.Carrier == p)
                    {
                        ServerTick now = ServerTick.Now;

                        BallData defaultBallData = new()
                        {
                            State = BallState.OnMap,
                            X = p.Position.X,
                            Y = p.Position.Y,
                            XSpeed = 0,
                            YSpeed = 0,
                            Carrier = newt || isLeaving ? null : p,
                            //Freq =  // TODO: maybe this should be set too?
                            Time = now,
                            LastUpdate = now,
                        };

                        prev = b;
                        b = defaultBallData;

                        bool allow = true;

                        // Consult advisors to allow other modules to affect the forced ball shooting.
                        var advisors = arena.GetAdvisors<IBallsAdvisor>();
                        foreach (var advisor in advisors)
                        {
                            // The true isForced parameter indicates that we're forcing the ball to be shot because the player left.
                            if (!(allow = advisor.AllowShootBall(arena, p, i, true, ref b)))
                                break;
                        }

                        if (!allow)
                        {
                            b = defaultBallData;
                        }

                        if (killer != null && ad.Settings.KillerIgnorePassDelay != 0)
                        {
                            extraInfo.LastKiller = killer;
                            extraInfo.KillerValidPickupTime = (uint)b.Time - (uint)ad.Settings.KillerIgnorePassDelay; // TODO: verify this and move it to an overloaded operator in the ServerTime struct
                        }
                        else
                        {
                            extraInfo.LastKiller = null;
                        }

                        SendBallPacket(arena, i);

                        // The ball is leaving the carrier no matter what, so the callback needs to be fired.
                        BallShootCallback.Fire(arena, arena, p, (byte)i);

                        MapCoordinate coordinate = new((short)(b.X / 16), (short)(b.Y / 16));
                        if (!newt
                            && b.Carrier != null
                            && _mapData.GetTile(arena, coordinate)?.IsGoal == true)
                        {
                            // Dropped an unneuted ball on a goal tile.
                            // Check whether it's a goal and if it is don't wait for the goal packet.
                            // Waiting for the goal packet is undesirable because it is a race between who has the better connection.
                            _logManager.LogP(LogLevel.Drivel, nameof(Balls), p, $"Dropped ball {i} on top of goal tile.");
                            HandleGoal(arena, b.Carrier, i, coordinate);
                        }
                    }
                    else if (newt && b.Carrier == p)
                    {
                        // If it's on the map, but last touched by the person, reset its last touched 
                        b.Carrier = null;
                        SendBallPacket(arena, i);
                    }
                }
            }
        }

        #region Helper Types

        private struct BallSpawn
        {
            public int X;
            public int Y;
            public int Radius;
        }

        private struct ExtraBallStateInfo
        {
            public Player LastKiller;
            public ServerTick KillerValidPickupTime;
        }

        private class ArenaData
        {
            /// <summary>
            /// The # of balls currently in play. 0 if the arena has no ball game.
            /// </summary>
            public int BallCount
            {
                get => Settings.BallCount;
                set => Settings.BallCount = value;
            }

            /// <summary>
            /// Array of ball states.
            /// </summary>
            public readonly BallData[] Balls = new BallData[MaxBalls];

            /// <summary>
            /// Array of previous ball states.
            /// </summary>
            public readonly BallData[] Previous = new BallData[MaxBalls];

            #region InternalBallData

            /// <summary>
            /// Array of spawn locations.
            /// </summary>
            public BallSpawn[] Spawns;

            /// <summary>
            /// Some extra info about balls that others shouldn't touch.
            /// </summary>
            public ExtraBallStateInfo[] ExtraBallStateInfo = new ExtraBallStateInfo[MaxBalls];

            /// <summary>
            /// When we last sent ball packets out to the arena.
            /// </summary>
            public ServerTick LastSendTime;

            /// <summary>
            /// Settings for the ball that are initially read from the config.
            /// </summary>
            public BallSettings Settings;

            /// <summary>
            /// If <see cref="SetBallCount"/> has been used, we don't want to override that if the settings change,
            /// especially since the Soccer:BallCount setting might not have been the one that changed.
            /// </summary>
            public bool BallCountOverridden;

            #endregion

            public readonly object Lock = new();
        }

        #endregion
    }
}
