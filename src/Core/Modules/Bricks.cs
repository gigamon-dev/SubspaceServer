using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for bricks.
    /// </summary>
    /// <remarks>
    /// Bricks are the temporary walls that can be placed onto the map which are assigned to a team, 
    /// such that players on that team can pass through it, but players on other teams are blocked.
    /// 
    /// <para>
    /// Normally, bricks are prizes given to players, which can be used/placed at their current location.
    /// To do this, the client sends a request (0x1C) to place a brick and the server then decides the 
    /// location to actually place it.  This is usually determined using the x and y coordinates from 
    /// the request, but that is not necessary; the server can choose do what it wants, even ingore the 
    /// request if it so chooses.
    /// </para>
    /// 
    /// <para>
    /// Since brick placement is server controlled, bricks have found other uses as well, such as: 
    /// <list type="bullet">
    /// <item>server controlled walls to resize the play area on a map</item>
    /// <item>server controlled 'doors' that can trigger on some event</item>
    /// <item>a way for the server to write text (see the 'brickwriter' module in ASSS)</item>
    /// </list>
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public class Bricks : IModule, IBrickManager, IBrickHandler
    {
        /// <summary>
        /// The maximum # of bricks to allow active at the same time.
        /// Attempts to place bricks beyond this limit will be ignored.
        /// </summary>
        private const int MaxActiveBricks = 256; // the Continuum client limit

        /// <summary>
        /// The maximum # of bricks to include in a packet, assuming it'll be sent reliably.
        /// </summary>
        private static readonly int MaxBricksPerPacket = (Constants.MaxPacket - ReliableHeader.Length - 1) / BrickData.Length;

        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken<IBrickManager> _iBrickManagerToken;
        private InterfaceRegistrationToken<IBrickHandler> _iBrickHandlerToken;

        private ArenaDataKey<ArenaBrickData> _adKey;

        /// <summary>
        /// Pool of <see cref="List{T}"/>s for <see cref="BrickLocation"/>.
        /// </summary>
        private ObjectPool<List<BrickLocation>> _brickLocationListPool;
        /// <summary>
        /// Pool of <see cref="List{T}"/>s for <see cref="BrickData"/>.
        /// </summary>
        private ObjectPool<List<BrickData>> _brickDataListPool;

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMapData mapData,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            DefaultObjectPoolProvider provider = new();
            _brickLocationListPool = provider.Create(new BrickLocationListPooledObjectPolicy());
            _brickDataListPool = provider.Create(new BrickDataListPooledObjectPolicy());

            _adKey = _arenaManager.AllocateArenaData<ArenaBrickData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            DoBrickModeCallback.Register(broker, Callback_DoBrickMode);

            _network.AddPacket(C2SPacketType.Brick, Packet_Brick);

            _iBrickManagerToken = broker.RegisterInterface<IBrickManager>(this);
            _iBrickHandlerToken = broker.RegisterInterface<IBrickHandler>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iBrickManagerToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iBrickHandlerToken) != 0)
                return false;

            _network.RemovePacket(C2SPacketType.Brick, Packet_Brick);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            DoBrickModeCallback.Unregister(broker, Callback_DoBrickMode);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        [ConfigHelp("Brick", "CountBricksAsWalls", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
            Description = "Whether bricks snap to the edges of other bricks (as opposed to only snapping to walls).")]
        [ConfigHelp("Brick", "BrickSpan", ConfigScope.Arena, typeof(int), DefaultValue = "10",
            Description = "The maximum length of a dropped brick.")]
        [ConfigHelp("Brick", "BrickMode", ConfigScope.Arena, typeof(BrickMode), DefaultValue = "Lateral",
            Description = "How bricks behave when they are dropped (" +
            "VIE = improved VIE style, " +
            "AHEAD = drop in a line ahead of player, " +
            "LATERAL = drop laterally across player, " +
            "CAGE = drop 4 bricks simultaneously to create a cage)")]
        [ConfigHelp("Brick", "BrickTime", ConfigScope.Arena, typeof(int), DefaultValue = "6000",
            Description = "How long bricks last (in ticks).")]
        [ConfigHelp("Routing", "WallResendCount", ConfigScope.Global, typeof(int), DefaultValue = "0", Range = "0-3",
            Description = "# of times a brick packet is sent unreliably, in addition to the reliable send.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                return;

            if (action == ArenaAction.Create)
            {
                abd.CurrentBrickId = 0;
                abd.LastTime = ServerTick.Now;
            }

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                abd.CountBricksAsWalls = _configManager.GetInt(arena.Cfg, "Brick", "CountBricksAsWalls", 1) != 0;
                abd.BrickSpan = _configManager.GetInt(arena.Cfg, "Brick", "BrickSpan", 10);
                abd.BrickMode = _configManager.GetEnum(arena.Cfg, "Brick", "BrickMode", BrickMode.Lateral);
                abd.BrickTime = (uint)_configManager.GetInt(arena.Cfg, "Brick", "BrickTime", 6000);
                abd.WallResendCount = Math.Clamp(_configManager.GetInt(arena.Cfg, "Routing", "WallResendCount", 0), 0, 3);

                // TODO: Add AntiBrickWarpDistance logic
            }
            else if (action == ArenaAction.Destroy)
            {
                abd.Bricks.Clear();
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                    return;

                lock (abd.Lock)
                {
                    ExpireBricks(player.Arena);
                    
                    // Send active bricks to the player.
                    SendToPlayerOrArena(player, null, abd.Bricks, 0);
                }
            }
        }

        private void Callback_DoBrickMode(Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (length < 1)
                return;

            if (bricks == null)
                return;

            if (_mapData.GetTile(arena, new Map.MapCoordinate(x, y)) != null)
                return; // can't place a brick on a tile

            // TODO: add other modes

            // TODO: Maybe add a mode that looks at p.Position.XSpeed and p.Position.YSpeed, take the absolute value and go perpendicular to the greater one?

            if (brickMode == BrickMode.Lateral)
            {
                // For making brick direction deterministic when rotation is at exactly 45 degrees (rotation of 5, 15, 25, or 35).
                bool isLastRotationClockwise = player.Position.IsLastRotationClockwise;

                BrickDirection direction = player.Position.Rotation switch
                {
                    > 35 or < 5 or (> 15 and < 25) => BrickDirection.Horizontal,
                    (> 5 and < 15) or (> 25 and < 35) => BrickDirection.Vertical,
                    5 or 25 => isLastRotationClockwise ? BrickDirection.Vertical : BrickDirection.Horizontal,
                    15 or 35 => isLastRotationClockwise ? BrickDirection.Horizontal : BrickDirection.Vertical,
                };

                // start at the center and move outward until we can't move further, or hit the desired max brick length
                MapCoordinate start = new(x, y);
                MapCoordinate end = new(x, y);

                bool isStartDone = false;
                bool isEndDone = false;
                int tileCount = 1;

                if (direction == BrickDirection.Vertical)
                {
                    while ((!isStartDone || !isEndDone) && tileCount < length)
                    {
                        if (!isStartDone && start.Y > 0)
                        {
                            MapCoordinate newStart = new(start.X, (short)(start.Y - 1));
                            if (_mapData.GetTile(arena, newStart) == null)
                            {
                                start = newStart;
                                tileCount++;
                            }
                            else
                            {
                                isStartDone = true;
                            }
                        }

                        if (tileCount == length)
                            break;

                        if (!isEndDone && end.Y < 1023)
                        {
                            MapCoordinate newEnd = new(end.X, (short)(end.Y + 1));
                            if (_mapData.GetTile(arena, newEnd) == null)
                            {
                                end = newEnd;
                                tileCount++;
                            }
                            else
                            {
                                isEndDone = true;
                            }
                        }
                    }
                }
                else if (direction == BrickDirection.Horizontal)
                {
                    while ((!isStartDone || !isEndDone) && tileCount < length)
                    {
                        if (!isStartDone && start.X > 0)
                        {
                            MapCoordinate newStart = new((short)(start.X - 1), start.Y);
                            if (_mapData.GetTile(arena, newStart) == null)
                            {
                                start = newStart;
                                tileCount++;
                            }
                            else
                            {
                                isStartDone = true;
                            }
                        }

                        if (tileCount == length)
                            break;

                        if (!isEndDone && end.X < 1023)
                        {
                            MapCoordinate newEnd = new((short)(end.X + 1), end.Y);
                            if (_mapData.GetTile(arena, newEnd) == null)
                            {
                                end = newEnd;
                                tileCount++;
                            }
                            else
                            {
                                isEndDone = true;
                            }
                        }
                    }
                }

                bricks.Add(new BrickLocation(start.X, start.Y, end.X, end.Y));
            }
        }

        private void Packet_Brick(Player player, byte[] data, int length, NetReceiveFlags flags)
        {
            Arena arena = player.Arena;

            if (length != C2S_Brick.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Bricks), player, $"Bad packet (length={length}).");
                return;
            }

            if (player.Status != PlayerState.Playing || player.Ship == ShipType.Spec || arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Bricks), player, $"Ignored request from bad state.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                return;

            ref C2S_Brick c2sBrick = ref MemoryMarshal.AsRef<C2S_Brick>(data);

            IBrickHandler brickHandler = _broker.GetInterface<IBrickHandler>();
            if (brickHandler == null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Bricks), "No brick handler found.");
                return;
            }

            try
            {
                lock (abd.Lock)
                {
                    List<BrickLocation> brickList = _brickLocationListPool.Get();

                    try
                    {
                        brickHandler.HandleBrick(player, c2sBrick.X, c2sBrick.Y, brickList);

                        // TODO: AntiBrickWarpDistance logic

                        DropBricks(arena, player.Freq, player, brickList);
                    }
                    finally
                    {
                        _brickLocationListPool.Return(brickList);
                    }
                }
            }
            finally
            {
                _broker.ReleaseInterface(ref brickHandler);
            }
        }

        void IBrickManager.DropBrick(Arena arena, short freq, short x1, short y1, short x2, short y2)
        {
            List<BrickLocation> brickList = _brickLocationListPool.Get();

            try
            {
                brickList.Add(new BrickLocation(x1, y1, x2, y2));
                DropBricks(arena, freq, null, brickList);
            }
            finally
            {
                _brickLocationListPool.Return(brickList);
            }
        }

        private void DropBricks(Arena arena, short freq, Player player, List<BrickLocation> bricks)
        {
            if (arena == null)
                return;

            if (bricks == null || bricks.Count <= 0)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                return;

            lock (abd.Lock)
            {
                ExpireBricks(arena);

                int available = MaxActiveBricks - abd.Bricks.Count;
                if (available <= 0 || available < bricks.Count)
                {
                    // We're already at the maximum # of bricks the client allows.
                    // Ignore it, otherwise we'll be out of sync with the clients.
                    _logManager.LogA(LogLevel.Drivel, nameof(Bricks), arena, $"Ignored brick drop. Unable to add {bricks.Count} {(bricks.Count == 1 ? "brick" : "bricks")} to {available}/{MaxActiveBricks} available slots.");
                    return;
                }

                List<BrickData> brickDataList = _brickDataListPool.Get();

                try
                {
                    foreach (BrickLocation request in bricks)
                    {
                        ServerTick startTime = ServerTick.Now;

                        // workaround for Continuum bug?
                        if (startTime <= abd.LastTime)
                            startTime = ++abd.LastTime;
                        else
                            abd.LastTime = startTime;

                        BrickData brickData = new(request.X1, request.Y1, request.X2, request.Y2, freq, abd.CurrentBrickId++, startTime);
                        brickDataList.Add(brickData);
                        abd.Bricks.Enqueue(brickData);

                        _logManager.LogA(LogLevel.Drivel, nameof(Bricks), arena, $"Brick dropped ({brickData.X1},{brickData.Y1})-({brickData.X2},{brickData.Y2}) (freq={brickData.Freq}) (id={brickData.BrickId})");

                        // TODO: CountBricksAsWalls
                        //if(abd.CountBricksAsWalls)
                        //_mapData.DoBrick()
                    }

                    SendToPlayerOrArena(null, arena, brickDataList, abd.WallResendCount);

                    BricksPlacedCallback.Fire(arena, arena, player, brickDataList);
                }
                finally
                {
                    _brickDataListPool.Return(brickDataList);
                }
            }
        }

        private void ExpireBricks(Arena arena)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                return;

            ServerTick now = ServerTick.Now;

            lock (abd.Lock)
            {
                while (abd.Bricks.TryPeek(out BrickData brick) 
                    && now >= brick.StartTime + abd.BrickTime)
                {
                    // TODO: CountBricksAsWalls
                    //if (abd.CountBricksAsWalls)
                    //_mapData.DoBrick()

                    abd.Bricks.Dequeue();
                }
            }
        }

        private void SendToPlayerOrArena<T>(Player player, Arena arena, T bricks, int wallResendCount) where T : IReadOnlyCollection<BrickData>
        {
            if (player == null && arena == null)
                return;

            if (bricks == null || bricks.Count <= 0)
                return;

            //
            // create and send packet(s)
            //

            Span<byte> packetSpan = stackalloc byte[1 + (Math.Clamp(bricks.Count, 1, MaxBricksPerPacket) * BrickData.Length)];
            packetSpan[0] = (byte)S2CPacketType.Brick;

            Span<BrickData> brickSpan = MemoryMarshal.Cast<byte, BrickData>(packetSpan[1..]);
            int index = 0;

            foreach (BrickData brick in bricks)
            {
                brickSpan[index++] = brick;

                if (index >= brickSpan.Length)
                {
                    // we have the maximum # of bricks that can be sent in a packet, send it
                    SendToPlayerOrArena(player, arena, packetSpan, wallResendCount);
                    index = 0;
                }
            }

            if (index > 0)
            {
                SendToPlayerOrArena(player, arena, packetSpan[..(1 + (index * BrickData.Length))], wallResendCount);
            }

            void SendToPlayerOrArena(Player player, Arena arena, Span<byte> data, int wallResendCount)
            {
                if (player == null && arena == null)
                    return;

                if (data.Length <= 0)
                    return;

                // send it unreliably, urgently, and allow it to be dropped (as many times as is configured)
                for (int i = 0; i < wallResendCount; i++)
                {
                    SendToPlayerOrArena(player, arena, data, NetSendFlags.Unreliable | NetSendFlags.Droppable | NetSendFlags.PriorityP5); // NOTE: PriorityP5 has the Urgent flag set.
                }

                // send it reliably (always)
                SendToPlayerOrArena(player, arena, data, NetSendFlags.Reliable);

                void SendToPlayerOrArena(Player player, Arena arena, Span<byte> data, NetSendFlags flags)
                {
                    if (player == null && arena == null)
                        return;

                    if (data.Length <= 0)
                        return;

                    if (player != null)
                        _network.SendToOne(player, data, flags);
                    else if (arena != null)
                        _network.SendToArena(arena, null, data, flags);
                }
            }
        }

        // Default implemention of IBrickHandler which fires the DoBrickModeCallback.
        // The callback provides a way for other modules to add new brick modes, without affecting existing ones.
        // It is a cleaner alternative than than having to override IBrickHandler.
        // NOTE: compared to ASSS, the callback passes Player instead of Arena.
        // This means more information about the player can be accessed (including rotation, velocity, etc.).
        // Of course, the arena can be accessed through the player too.
        void IBrickHandler.HandleBrick(Player player, short x, short y, IList<BrickLocation> bricks)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData abd))
                return;

            DoBrickModeCallback.Fire(arena, player, abd.BrickMode, x, y, abd.BrickSpan, bricks);
        }

        #region Helper types

        public class ArenaBrickData : IPooledExtraData
        {
            /// <summary>
            /// Whether bricks should snap to the edges of other bricks
            /// </summary>
            public bool CountBricksAsWalls;

            /// <summary>
            /// Maximum # of tiles a brick should span.
            /// </summary>
            public int BrickSpan;

            /// <summary>
            /// The algorithm to use when placing a brick.
            /// </summary>
            public BrickMode BrickMode;

            /// <summary>
            /// How long a brick lasts.
            /// </summary>
            public uint BrickTime;

            /// <summary>
            /// # of times a brick packet is sent unreliably, in addition to the reliable send.
            /// </summary>
            public int WallResendCount;

            /// <summary>
            /// Id to use for the next brick.
            /// </summary>
            public short CurrentBrickId;

            /// <summary>
            /// The time (ticks) of the last brick that was placed.
            /// </summary>
            /// <remarks>
            /// Assuming (based on notes in ASSS) it's a limitation of Continuum only allowing one brick to be placed for a given time.
            /// That is, Continuum possibly ignores BrickId and uses time as the identifier?
            /// </remarks>
            public ServerTick LastTime;

            /// <summary>
            /// Queue of bricks that have been placed.
            /// </summary>
            public readonly Queue<BrickData> Bricks = new(MaxActiveBricks);

            public readonly object Lock = new();

            public void Reset()
            {
                lock (Lock)
                {
                    CountBricksAsWalls = false;
                    BrickSpan = 0;
                    BrickMode = BrickMode.VIE;
                    BrickTime = 0;
                    WallResendCount = 0;
                    CurrentBrickId = 0;
                    LastTime = 0;
                    Bricks.Clear();
                }
            }
        }

        public enum BrickMode
        {
            VIE,
            Ahead,
            Lateral,
            Cage,
        }

        private enum BrickDirection
        {
            Vertical,
            Horizontal,
        }

        private class BrickLocationListPooledObjectPolicy : PooledObjectPolicy<List<BrickLocation>>
        {
            public int InitialCapacity { get; set; } = 8;

            public override List<BrickLocation> Create()
            {
                return new List<BrickLocation>(InitialCapacity);
            }

            public override bool Return(List<BrickLocation> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class BrickDataListPooledObjectPolicy : PooledObjectPolicy<List<BrickData>>
        {
            public int InitialCapacity { get; set; } = 8;

            public override List<BrickData> Create()
            {
                return new List<BrickData>(InitialCapacity);
            }

            public override bool Return(List<BrickData> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        #endregion
    }
}
