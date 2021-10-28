using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    public class Bricks : IModule, IBrickManager, IBrickHandler
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken _iBrickHandlerToken;

        private int _adKey;

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

            _adKey = _arenaManager.AllocateArenaData<ArenaBrickData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            DoBrickModeCallback.Register(broker, Callback_DoBrickMode);

            _network.AddPacket(C2SPacketType.Brick, Packet_Brick);

            _iBrickHandlerToken = broker.RegisterInterface<IBrickHandler>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IBrickHandler>(ref _iBrickHandlerToken);

            _network.RemovePacket(C2SPacketType.Brick, Packet_Brick);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            DoBrickModeCallback.Unregister(broker, Callback_DoBrickMode);

            _arenaManager.FreeArenaData(_adKey);

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
            if (arena[_adKey] is not ArenaBrickData abd)
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
                abd.WallResendCount = _configManager.GetInt(arena.Cfg, "Routing", "WallResendCount", 0);

                if (abd.WallResendCount < 0)
                    abd.WallResendCount = 0;

                if (abd.WallResendCount > 3)
                    abd.WallResendCount = 3;

                // TODO: Add AntiBrickWarpDistance logic
            }
            else if (action == ArenaAction.Destroy)
            {
                abd.Bricks.Clear();
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
                SendOldBricks(p);
        }

        private void Callback_DoBrickMode(Player p, BrickMode brickMode, short x, short y, int length, in ICollection<Brick> bricks)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
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
                bool isLastRotationClockwise = p.Position.IsLastRotationClockwise;

                BrickDirection direction = p.Position.Rotation switch
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

                bricks.Add(new Brick(start.X, start.Y, end.X, end.Y));
            }
        }

        private void Packet_Brick(Player p, byte[] data, int length)
        {
            Arena arena = p.Arena;

            if (length != C2SBrick.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Bricks), p, $"Bad packet (length={length}).");
                return;
            }

            if (p.Status != PlayerState.Playing || p.Ship == ShipType.Spec || arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Bricks), p, $"Ignored request from bad state.");
                return;
            }

            if (arena[_adKey] is not ArenaBrickData abd)
                return;

            ref C2SBrick c2sBrick = ref MemoryMarshal.AsRef<C2SBrick>(data);

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
                    ExpireBricks(arena);

                    List<Brick> brickList = _objectPoolManager.BrickListPool.Get();
                    ICollection<Brick> brickCollection = brickList;

                    try
                    {
                        brickHandler.HandleBrick(p, c2sBrick.X, c2sBrick.Y, in brickCollection);

                        // TODO: AntiBrickWarpDistance logic

                        foreach (Brick brick in brickList)
                        {
                            DropBrick(arena, p.Freq, brick.X1, brick.Y1, brick.X2, brick.Y2);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.BrickListPool.Return(brickList);
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
            ExpireBricks(arena);
            DropBrick(arena, freq, x1, y1, x2, y2);
        }

        private void DropBrick(Arena arena, short freq, short x1, short y1, short x2, short y2)
        {
            if (arena == null)
                return;

            if (arena[_adKey] is not ArenaBrickData abd)
                return;

            lock (abd.Lock)
            {
                S2CBrick packet = new(x1, y1, x2, y2, freq, abd.CurrentBrickId++, ServerTick.Now);

                // workaround for Continnum bug?
                if (packet.StartTime <= abd.LastTime)
                    packet.StartTime = ++abd.LastTime;
                else
                    abd.LastTime = packet.StartTime;

                abd.Bricks.Enqueue(packet);

                ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

                // send it unreliably, urgently, and allow it to be dropped (as many times as is configured)
                for (int i = 0; i < abd.WallResendCount; i++)
                    _network.SendToArena(arena, null, data, NetSendFlags.Unreliable | NetSendFlags.Urgent | NetSendFlags.Droppable);

                // send it reliably (always)
                _network.SendToArena(arena, null, data, NetSendFlags.Reliable);

                _logManager.LogA(LogLevel.Drivel, nameof(Bricks), arena, $"Brick dropped ({x1},{y1})-({x2},{y2}) (freq={freq}) (id={packet.BrickId})");

                //if(abd.CountBricksAsWalls)
                //_mapData.DoBrick()
            }
        }

        // call with lock held
        private void ExpireBricks(Arena arena)
        {
            if (arena == null)
                return;

            if (arena[_adKey] is not ArenaBrickData abd)
                return;

            ServerTick now = ServerTick.Now;

            lock (abd.Lock)
            {
                while (abd.Bricks.TryPeek(out S2CBrick packet) && now > packet.StartTime + abd.BrickTime)
                {
                    //if (abd.CountBricksAsWalls)
                    //_mapData.DoBrick()

                    abd.Bricks.Dequeue();
                }
            }
        }

        private void SendOldBricks(Player p)
        {
            if (p == null)
                return;

            if (p?.Arena[_adKey] is not ArenaBrickData abd)
                return;

            lock (abd.Lock)
            {
                ExpireBricks(p.Arena);

                foreach (S2CBrick packet in abd.Bricks)
                {
                    S2CBrick copy = packet; // TODO: is there a way to avoid the copy?
                    _network.SendToOne(p, ref copy, NetSendFlags.Reliable);
                }
            }
        }

        // Default implemention of IBrickHandler which fires the DoBrickModeCallback.
        // The callback allows other modules to add handling for new brick modes server-wide, rather than having to override IBrickHandler for particular arenas.
        // I did, however, change DoBrickModeCallback to take Player instead of Arena since my desired implementation needed to access more than player rotation.
        void IBrickHandler.HandleBrick(Player p, short x, short y, in ICollection<Brick> bricks)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (arena[_adKey] is not ArenaBrickData abd)
                return;

            DoBrickModeCallback.Fire(arena, p, abd.BrickMode, x, y, abd.BrickSpan, in bricks);
        }

        public class ArenaBrickData
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
            public readonly Queue<S2CBrick> Bricks = new();

            public readonly object Lock = new();
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
    }
}
