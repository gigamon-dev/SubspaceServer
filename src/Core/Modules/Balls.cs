using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using SoccerSettings = SS.Core.ConfigHelp.Constants.Arena.Soccer;

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
    [CoreModuleInfo]
    public sealed class Balls(
        IComponentBroker broker,
        IArenaManager arenaManager,
        IConfigManager configManager,
        ILogManager logManager,
        IMainloopTimer mainloopTimer,
        IMapData mapData,
        INetwork network,
        IPlayerData playerData,
        IPrng prng) : IModule, IBalls
    {
        /// <summary>
        /// Continuum supports up to a maximum of 8 balls.
        /// </summary>
        private const int MaxBalls = 8;
        private const NetSendFlags BallSendFlags = NetSendFlags.PriorityP4;

        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly IMapData _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private readonly IPrng _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        private InterfaceRegistrationToken<IBalls>? _iBallsToken;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
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

        bool IModule.Unload(IComponentBroker broker)
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

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region IBalls

        bool IBalls.TryGetBallSettings(Arena arena, out BallSettings ballSettings)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                ballSettings = default;
                return false;
            }

            lock (ad.Lock)
            {
                ballSettings = ad.Settings; // copy
                ballSettings.BallCount = ad.BallCount;
                return true;
            }
        }

        bool IBalls.TrySetBallCount(Arena arena, int? ballCount)
        {
            if (arena == null)
                return false;

            if (ballCount < 0 || ballCount > MaxBalls)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            lock (ad.Lock)
            {
                bool isOverride = ballCount != null;

                ballCount ??= ad.Settings.BallCount;

                if (TryChangeBallCount(arena, ballCount.Value))
                {
                    ad.BallCountOverridden = isOverride;
                    return true;
                }

                return false;
            }
        }

        bool IBalls.TryGetBallData(Arena arena, int ballId, out BallData ballData)
        {
            if (arena == null
                || ballId < 0
                || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

        [ConfigHelp<int>("Soccer", "NewGameDelay", ConfigScope.Arena, Default = -3000,
            Description = "How long to wait between games (in ticks). If this is negative, the actual delay is random, between zero and the absolute value.")]
        void IBalls.EndGame(Arena arena)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                for (int i = 0; i < ad.BallCount; i++)
                {
                    PhaseBall(arena, i);
                    ad.Balls[i].State = BallState.Waiting;
                    ad.Balls[i].Carrier = null;
                }

                int newGameDelay = _configManager.GetInt(arena.Cfg!, "Soccer", "NewGameDelay", SoccerSettings.NewGameDelay.Default);
                if (newGameDelay < 0)
                    newGameDelay = _prng.Number(0, -newGameDelay);

                for (int i = 0; i < ad.BallCount; i++)
                    ad.Balls[i].Time = ServerTick.Now + (uint)newGameDelay;
            }

            IPersistExecutor? persistExecutor = _broker.GetInterface<IPersistExecutor>();
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

        void IBalls.GetGoalInfo(Arena arena, short freq, TileCoordinates coordinates, out bool isScorable, out short? ownerFreq)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!_mapData.GetTile(arena, coordinates).IsGoal)
            {
                isScorable = false;
                ownerFreq = null;
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                isScorable = false;
                ownerFreq = null;
                return;
            }

            SoccerMode mode;

            lock (ad.Lock)
            {
                mode = ad.Settings.Mode;
            }

            (short x, short y) = coordinates;

            switch (mode)
            {
                case SoccerMode.All:
                    isScorable = true; // any freq can score on it
                    ownerFreq = null; // it's not any team's own goal
                    return;

                case SoccerMode.LeftRight:
                    freq %= 2;
                    ownerFreq = x < 512 ? (short)0 : (short)1;
                    isScorable = freq != ownerFreq;
                    return;

                case SoccerMode.TopBottom:
                    freq %= 2;
                    ownerFreq = y < 512 ? (short)0 : (short)1;
                    isScorable = freq != ownerFreq;
                    return;

                case SoccerMode.QuadrantsDefend1:
                    freq %= 4;

                    if (x < 512)
                    {
                        if (y < 512)
                        {
                            // top left
                            ownerFreq = 0;
                        }
                        else
                        {
                            // bottom left
                            ownerFreq = 2;
                        }
                    }
                    else
                    {
                        if (y < 512)
                        {
                            // top right
                            ownerFreq = 1;
                        }
                        else
                        {
                            // bottom right
                            ownerFreq = 3;
                        }
                    }

                    isScorable = freq != ownerFreq;
                    return;

                case SoccerMode.QuadrantsDefend3:
                    freq %= 4;

                    if (x < 512)
                    {
                        if (y < 512)
                        {
                            // top left
                            isScorable = freq == 0;
                        }
                        else
                        {
                            // bottom left
                            isScorable = freq == 2;
                        }
                    }
                    else
                    {
                        if (y < 512)
                        {
                            // top right
                            isScorable = freq == 1;
                        }
                        else
                        {
                            // bottom right
                            isScorable = freq == 3;
                        }
                    }

                    ownerFreq = null; // no team is the sole owner
                    return;

                case SoccerMode.SidesDefend1:
                    freq %= 4;

                    if (x < y)
                    {
                        if (x < 1024 - y)
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
                    else
                    {
                        if (x < 1024 - y)
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

                    isScorable = ownerFreq != freq;
                    return;

                case SoccerMode.SidesDefend3:
                    freq %= 4;

                    if (x < y)
                    {
                        if (x < 1024 - y)
                        {
                            // left
                            isScorable = freq == 0;
                        }
                        else
                        {
                            // bottom
                            isScorable = freq == 1;
                        }
                    }
                    else
                    {
                        if (x < 1024 - y)
                        {
                            // top
                            isScorable = freq == 2;
                        }
                        else
                        {
                            // right
                            isScorable = freq == 3;
                        }
                    }

                    ownerFreq = null; // no team is the sole owner
                    return;

                default:
                    throw new Exception("Invalid Soccer:Mode.");
            }
        }

        #endregion

        #region Callback handlers

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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
                    int newBallCount = LoadBallSettings(arena);

                    // If the ball count changed but it wasn't changed by a module or command, allow the new setting to change the ball count.
                    if (newBallCount != ad.BallCount && !ad.BallCountOverridden)
                    {
                        TryChangeBallCount(arena, newBallCount);
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            // Players entering will automatically get ball information by packets sent by the timer. Nothing special needed here for that.

            if (action == PlayerAction.LeaveArena)
                CleanupAfter(arena, player, null, true, true);
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            CleanupAfter(player.Arena, player, null, true, false);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                CleanupAfter(arena, killed, killer, !ad.Settings.DeathScoresGoal, false);
            }
        }

        #endregion

        #region Packet handlers

        private void Packet_PickupBall(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != C2S_PickupBall.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), player, $"Bad ball pick up packet (length={data.Length}).");
                return;
            }

            Arena? arena = player.Arena;
            if (arena == null || player.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, $"Ball pick up packet from bad arena or status (status={player.Status}).");
                return;
            }

            if (player.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: ball pick up from spectator mode.");
                return;
            }

            ref readonly C2S_PickupBall c2s = ref MemoryMarshal.AsRef<C2S_PickupBall>(data);

            if (player.Flags.NoFlagsBalls)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Balls), player, $"Too lagged to pick up ball {c2s.BallId}.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                byte ballId = c2s.BallId;

                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, $"State sync problem: tried to pick up nonexistent ball {ballId}");
                    return;
                }

                ref BallData bd = ref ad.Balls[ballId];
                ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[ballId];

                // Make sure someone else didn't get it first.
                if (bd.State != BallState.OnMap)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: tried to pick up a ball that can't be picked up at the moment.");
                    return;
                }

                if (c2s.Time != bd.Time && (player != extraInfo.LastKiller || c2s.Time != extraInfo.KillerValidPickupTime))
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: tried to pick up a ball from stale coords.");
                    return;
                }

                // Make sure the player doesn't carry more than one ball.
                for (int i = 0; i < ad.BallCount; i++)
                {
                    if (ad.Balls[i].Carrier == player
                        && ad.Balls[i].State == BallState.Carried
                        && i != ballId)
                    {
                        return;
                    }
                }

                BallData defaultBallData = bd;

                bd.State = BallState.Carried;
                bd.X = player.Position.X;
                bd.Y = player.Position.Y;
                bd.XSpeed = 0;
                bd.YSpeed = 0;
                bd.Carrier = player;
                bd.Freq = player.Freq;
                bd.Time = 0;
                bd.LastUpdate = ServerTick.Now;

                bool allow = true;

                // Consult advisors to allow other modules to affect the pickup.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (!(allow = advisor.AllowPickupBall(arena, player, ballId, ref bd)))
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
                    BallPickupCallback.Fire(arena, arena, player, ballId);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), player, $"Picked up ball {ballId}.");
                }
            }
        }

        private void Packet_ShootBall(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != BallPacket.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), player, $"Bad ball shoot packet (length={data.Length}).");
                return;
            }

            Arena? arena = player.Arena;
            if (arena == null || player.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "Ball fire packet from bad arena or status.");
                return;
            }

            if (player.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: ball shoot packet from spectator mode.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ref readonly BallPacket c2s = ref MemoryMarshal.AsRef<BallPacket>(data);
            byte ballId = c2s.BallId;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, $"State sync problem: tried to shoot nonexistent ball {ballId}");
                    return;
                }

                ref BallData bd = ref ad.Balls[ballId];
                ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[ballId];

                if (bd.State != BallState.Carried || bd.Carrier != player)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: tried to shoot ball he wasn't carrying.");
                    return;
                }

                BallData defaultBallData = bd;

                bd.State = BallState.OnMap;
                bd.X = c2s.X;
                bd.Y = c2s.Y;
                bd.XSpeed = c2s.XSpeed;
                bd.YSpeed = c2s.YSpeed;
                bd.Freq = player.Freq;
                bd.Time = c2s.Time;
                bd.LastUpdate = ServerTick.Now;

                bool allow = true;

                // Consult advisors to allow other modules to affect the ball shot.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (!(allow = advisor.AllowShootBall(arena, player, ballId, false, ref bd)))
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
                    BallShootCallback.Fire(arena, arena, player, ballId);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), player, $"Shot ball {ballId}.");

                    TileCoordinates coordinates = new((short)(bd.X / 16), (short)(bd.Y / 16));
                    if (bd.Carrier != null
                        && _mapData.GetTile(arena, coordinates).IsGoal)
                    {
                        // Shot a ball on top of a goal tile.
                        // Check whether it's a goal and if it is don't wait for the goal packet.
                        // Waiting for the goal packet is undesirable because it is a race between who has the better connection.
                        _logManager.LogP(LogLevel.Drivel, nameof(Balls), player, $"Shot ball {ballId} on top of goal tile.");
                        HandleGoal(arena, bd.Carrier, ballId, coordinates);
                    }
                }
            }
        }

        private void Packet_Goal(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (data.Length != C2S_Goal.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Balls), player, $"Bad ball goal packet (length={data.Length}).");
                return;
            }

            Arena? arena = player.Arena;
            if (arena == null || player.Status != PlayerState.Playing)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "Ball goal packet from bad arena or status.");
                return;
            }

            if (player.Ship >= ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: ball goal packet from spectator mode.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ref readonly C2S_Goal c2s = ref MemoryMarshal.AsRef<C2S_Goal>(data);
            int ballId = c2s.BallId;

            lock (ad.Lock)
            {
                if (ballId >= ad.BallCount)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, $"State sync problem: tried a goal for nonexistent ball {ballId}");
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
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: sent goal for carried ball.");
                    return;
                }

                if (player != bd.Carrier)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Balls), player, "State sync problem: sent goal for ball he didn't shoot.");
                    return;
                }

                HandleGoal(arena, player, ballId, new TileCoordinates(c2s.X, c2s.Y));
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

                    if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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
                                var position = b.Carrier!.Position;
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

        private const string MultipleBallsNote = """
            N is omitted from the first setting. N can be from 1 to 7 for subsequent settings.
            If there are more balls than spawn settings defined, later balls will repeat the spawns in order.
            For example, with 3 spawns, the fourth ball uses the first spawn, the fifth ball uses the second.
            If only part of a spawn is undefined, that part will default to the first spawn's setting.
            """;

        [ConfigHelp<int>("Soccer", "BallCount", ConfigScope.Arena, Default = 0, Min = 0, Max = 8,
            Description = "The number of balls in this arena.")]
        // Note: Soccer:Mode is a client setting. So, it's [ConfigHelp] is in ClientSettingsConfig.cs
        [ConfigHelp<int>("Soccer", "SpawnX", ConfigScope.Arena, Default = 512, Min = 0, Max = 1023,
            Description = $"""
                The X coordinate that the ball spawns at (in tiles).
                This can be set for each ball separately: SpawnX[N].
                {MultipleBallsNote}
                """)]
        [ConfigHelp<int>("Soccer", "SpawnY", ConfigScope.Arena, Default = 512, Min = 0, Max = 1023,
            Description = $"""
                The Y coordinate that the ball spawns at (in tiles).
                This can be set for each ball separately: SpawnY[N].
                {MultipleBallsNote}
                """)]
        [ConfigHelp<int>("Soccer", "SpawnRadius", ConfigScope.Arena, Default = 20, Min = 0, Max = 1024,
            Description = $"""
                How far from the spawn center the ball can spawn (in tiles).
                This can be set for each ball separately: SpawnRadius[N].
                {MultipleBallsNote}
                """)]
        [ConfigHelp<int>("Soccer", "SendTime", ConfigScope.Arena, Default = 100, Min = 25, Max = 500,
            Description = "How often the server sends ball positions (in ticks).")]
        [ConfigHelp<int>("Soccer", "GoalDelay", ConfigScope.Arena, Default = 0,
            Description = "How long after a goal before the ball appears (in ticks).")]
        [ConfigHelp<bool>("Soccer", "AllowGoalByDeath", ConfigScope.Arena, Default = false,
            Description = "Whether a goal is scored if a player dies while carrying the ball on a goal tile.")]
        [ConfigHelp<int>("Soccer", "KillerIgnorePassDelay", ConfigScope.Arena, Default = 0,
            Description = "How much 'pass delay' should be trimmed off for someone killing a ball carrier.")]
        private int LoadBallSettings(Arena arena)
        {
            if (arena == null)
                return 0;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return 0;

            int newSpawnCount = 1;

            lock (ad.Lock)
            {
                ConfigHandle ch = arena.Cfg!;
                ad.Settings.BallCount = Math.Clamp(_configManager.GetInt(ch, "Soccer", "BallCount", SoccerSettings.BallCount.Default), SoccerSettings.BallCount.Min, SoccerSettings.BallCount.Max);
                ad.Settings.Mode = _configManager.GetEnum(ch, "Soccer", "Mode", SoccerMode.All);

                Span<BallSpawn> spawns = stackalloc BallSpawn[MaxBalls];

                spawns[0].X = int.Clamp(_configManager.GetInt(ch, "Soccer", "SpawnX", SoccerSettings.SpawnX.Default), SoccerSettings.SpawnX.Min, SoccerSettings.SpawnX.Max);
                spawns[0].Y = int.Clamp(_configManager.GetInt(ch, "Soccer", "SpawnY", SoccerSettings.SpawnY.Default), SoccerSettings.SpawnY.Min, SoccerSettings.SpawnY.Max);
                spawns[0].Radius = int.Clamp(_configManager.GetInt(ch, "Soccer", "SpawnRadius", SoccerSettings.SpawnRadius.Default), SoccerSettings.SpawnRadius.Min, SoccerSettings.SpawnRadius.Max);

                Span<char> xName = stackalloc char["SpawnX#".Length];
                Span<char> yName = stackalloc char["SpawnY#".Length];
                Span<char> rName = stackalloc char["SpawnRadius#".Length];

                "SpawnX".CopyTo(xName);
                "SpawnY".CopyTo(yName);
                "SpawnRadius".CopyTo(rName);

                for (int i = 1; i < MaxBalls; i++)
                {
                    xName[^1] = yName[^1] = rName[^1] = (char)('0' + i);

                    spawns[i].X = _configManager.GetInt(ch, "Soccer", xName, -1);
                    spawns[i].Y = _configManager.GetInt(ch, "Soccer", yName, -1);

                    if (spawns[i].X == -1 && spawns[i].Y == -1)
                    {
                        break;
                    }

                    newSpawnCount++;

                    if (spawns[i].X == -1)
                    {
                        spawns[i].X = spawns[0].X;
                    }
                    else if (spawns[i].Y == -1)
                    {
                        spawns[i].Y = spawns[0].Y;
                    }

                    spawns[i].Radius = _configManager.GetInt(ch, "Soccer", rName, -1);

                    if (spawns[i].Radius == -1)
                    {
                        spawns[i].Radius = spawns[0].Radius;
                    }

                    spawns[i].X = int.Clamp(spawns[i].X, SoccerSettings.SpawnX.Min, SoccerSettings.SpawnX.Max);
                    spawns[i].Y = int.Clamp(spawns[i].Y, SoccerSettings.SpawnY.Min, SoccerSettings.SpawnY.Max);
                    spawns[i].Radius = int.Clamp(spawns[i].Radius, SoccerSettings.SpawnRadius.Min, SoccerSettings.SpawnRadius.Max);
                }

                ad.SetSpawns(spawns[..newSpawnCount]);

                ad.Settings.SendTime = int.Clamp(_configManager.GetInt(ch, "Soccer", "SendTime", SoccerSettings.SendTime.Default), SoccerSettings.SendTime.Min, SoccerSettings.SendTime.Max);
                ad.Settings.RespawnTimeAfterGoal = _configManager.GetInt(ch, "Soccer", "GoalDelay", SoccerSettings.GoalDelay.Default);
                ad.Settings.DeathScoresGoal = _configManager.GetBool(ch, "Soccer", "AllowGoalByDeath", SoccerSettings.AllowGoalByDeath.Default);
                ad.Settings.KillerIgnorePassDelay = _configManager.GetInt(ch, "Soccer", "KillerIgnorePassDelay", SoccerSettings.KillerIgnorePassDelay.Default);

                return ad.Settings.BallCount;
            }
        }

        private bool TryChangeBallCount(Arena arena, int newBallCount)
        {
            if (arena == null)
                return false;

            if (newBallCount < 0 || newBallCount > MaxBalls)
                return false;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

                // wrap around, don't clip, so radii of 2048 from a corner work properly
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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            // Keep information consistent, player's freq for owned balls, freq of -1 for unowned balls.
            newPos.Freq = (newPos.Carrier != null) ? newPos.Carrier.Freq : (short)-1;

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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
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

                BallPacketSentCallback.Fire(arena, arena, ref bp);
            }
        }

        private void HandleGoal(Arena arena, Player player, int ballId, TileCoordinates goalCoordinates)
        {
            if (arena == null)
                return;

            if (player == null)
                return;

            if (ballId < 0)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                if (ballId > ad.BallCount)
                    return;

                ref BallData bd = ref ad.Balls[ballId];
                BallData newBallData = bd;

                bool block = false;

                // Consult advisors to allow other modules to affect the goal.
                var advisors = arena.GetAdvisors<IBallsAdvisor>();
                foreach (var advisor in advisors)
                {
                    bool allow = advisor.AllowGoal(arena, player, ballId, goalCoordinates, ref bd);
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

                    BallGoalCallback.Fire(arena, arena, player, (byte)ballId, goalCoordinates);

                    _logManager.LogP(LogLevel.Info, nameof(Balls), player, $"Goal with ball {ballId} at {goalCoordinates}.");
                }
            }
        }

        private void CleanupAfter(Arena? arena, Player player, Player? killer, bool newt, bool isLeaving)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            // Make sure that if someone leaves, any balls the player was carrying drop.
            lock (ad.Lock)
            {
                for (int i = 0; i < ad.BallCount; i++)
                {
                    ref BallData b = ref ad.Balls[i];
                    ref BallData prev = ref ad.Previous[i];
                    ref ExtraBallStateInfo extraInfo = ref ad.ExtraBallStateInfo[i];

                    if (extraInfo.LastKiller == player)
                    {
                        // This info no longer applies to this player.
                        extraInfo.LastKiller = null;
                    }

                    if (isLeaving && prev.Carrier == player)
                    {
                        // Prevent stale player from appearing in historical data.
                        prev.Carrier = null;
                    }

                    if (b.State == BallState.Carried && b.Carrier == player)
                    {
                        ServerTick now = ServerTick.Now;

                        BallData defaultBallData = new()
                        {
                            State = BallState.OnMap,
                            X = player.Position.X,
                            Y = player.Position.Y,
                            XSpeed = 0,
                            YSpeed = 0,
                            Carrier = newt || isLeaving ? null : player,
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
                            if (!(allow = advisor.AllowShootBall(arena, player, i, true, ref b)))
                                break;
                        }

                        if (!allow)
                        {
                            b = defaultBallData;
                        }

                        if (killer != null && ad.Settings.KillerIgnorePassDelay != 0)
                        {
                            extraInfo.LastKiller = killer;
                            extraInfo.KillerValidPickupTime = b.Time - (uint)ad.Settings.KillerIgnorePassDelay;
                        }
                        else
                        {
                            extraInfo.LastKiller = null;
                        }

                        SendBallPacket(arena, i);

                        // The ball is leaving the carrier no matter what, so the callback needs to be fired.
                        BallShootCallback.Fire(arena, arena, player, (byte)i);

                        TileCoordinates coordinates = new((short)(b.X / 16), (short)(b.Y / 16));
                        if (!newt
                            && b.Carrier != null
                            && _mapData.GetTile(arena, coordinates).IsGoal)
                        {
                            // Dropped an unneuted ball on a goal tile.
                            // Check whether it's a goal and if it is don't wait for the goal packet.
                            // Waiting for the goal packet is undesirable because it is a race between who has the better connection.
                            _logManager.LogP(LogLevel.Drivel, nameof(Balls), player, $"Dropped ball {i} on top of goal tile.");
                            HandleGoal(arena, b.Carrier, i, coordinates);
                        }
                    }
                    else if (newt && b.Carrier == player)
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
            public Player? LastKiller;
            public ServerTick KillerValidPickupTime;
        }

        private class ArenaData : IResettable
        {
            /// <summary>
            /// The # of balls currently in play. 0 if the arena has no ball game.
            /// </summary>
            public int BallCount;

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
            private readonly BallSpawn[] _spawns = new BallSpawn[MaxBalls];

            /// <summary>
            /// The # of <see cref="_spawns"/> that are populated.
            /// </summary>
            private int _spawnCount;

            /// <summary>
            /// Spawn locations.
            /// </summary>
            public ReadOnlySpan<BallSpawn> Spawns => new(_spawns, 0, _spawnCount);

            /// <summary>
            /// Some extra info about balls that others shouldn't touch.
            /// </summary>
            public readonly ExtraBallStateInfo[] ExtraBallStateInfo = new ExtraBallStateInfo[MaxBalls];

            /// <summary>
            /// When we last sent ball packets out to the arena.
            /// </summary>
            public ServerTick LastSendTime;

            /// <summary>
            /// Settings for the ball that are initially read from the config.
            /// </summary>
            public BallSettings Settings;

            /// <summary>
            /// If <see cref="IBalls.TrySetBallCount"/> has been used to override with a value that differs from the Soccer:BallCount setting.
            /// When overridden, the ball count will not be affected when there's a config change.
            /// </summary>
            public bool BallCountOverridden;

            #endregion

            public readonly Lock Lock = new();

            public void SetSpawns(ReadOnlySpan<BallSpawn> spawns)
            {
                if (spawns.Length > _spawns.Length)
                    spawns = spawns[.._spawns.Length];

                spawns.CopyTo(_spawns);
                _spawnCount = spawns.Length;
            }

            public bool TryReset()
            {
                lock (Lock)
                {
                    BallCount = 0;
                    Array.Clear(Balls);
                    Array.Clear(Previous);
                    Array.Clear(_spawns);
                    _spawnCount = 0;
                    Array.Clear(ExtraBallStateInfo);
                    LastSendTime = default;
                    Settings = default;
                    BallCountOverridden = false;
                }

                return true;
            }
        }

        #endregion
    }
}
