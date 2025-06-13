﻿using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using BrickSettings = SS.Core.ConfigHelp.Constants.Arena.Brick;
using RoutingSettings = SS.Core.ConfigHelp.Constants.Arena.Routing;

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
    /// the request, but that is not necessary; the server can choose do what it wants, even ignore the 
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
    public sealed class Bricks(
        IComponentBroker broker,
        IArenaManager arenaManager,
        IConfigManager configManager,
        ILogManager logManager,
        IMapData mapData,
        INetwork network,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData,
        IPrng prng) : IModule, IBrickManager, IBrickHandler
    {
        /// <summary>
        /// The maximum # of bricks to allow active at the same time.
        /// Attempts to place bricks beyond this limit will be ignored.
        /// </summary>
        public const int MaxActiveBricks = 256; // the Continuum client limit

        /// <summary>
        /// The maximum # of bricks to include in a packet, assuming it'll be sent reliably.
        /// </summary>
        private static readonly int MaxBricksPerPacket = (Constants.MaxPacket - ReliableHeader.Length - 1) / BrickData.Length;

        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMapData _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private readonly IPrng _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        private InterfaceRegistrationToken<IBrickManager>? _iBrickManagerToken;
        private InterfaceRegistrationToken<IBrickHandler>? _iBrickHandlerToken;

        private ArenaDataKey<ArenaBrickData> _adKey;

        /// <summary>
        /// Pool of <see cref="List{T}"/>s for <see cref="BrickLocation"/>.
        /// </summary>
        private readonly DefaultObjectPool<List<BrickLocation>> _brickLocationListPool = new(new ListPooledObjectPolicy<BrickLocation>() { InitialCapacity = 8 });

        /// <summary>
        /// Pool of <see cref="List{T}"/>s for <see cref="BrickData"/>.
        /// </summary>
        private readonly DefaultObjectPool<List<BrickData>> _brickDataListPool = new(new ListPooledObjectPolicy<BrickData>() { InitialCapacity = 8 });

        #region Module methods

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaBrickData>();

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            DoBrickModeCallback.Register(_broker, Callback_DoBrickMode);

            _network.AddPacket(C2SPacketType.Brick, Packet_Brick);

            _iBrickManagerToken = _broker.RegisterInterface<IBrickManager>(this);
            _iBrickHandlerToken = _broker.RegisterInterface<IBrickHandler>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
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

        [ConfigHelp<bool>("Brick", "CountBricksAsWalls", ConfigScope.Arena, Default = true,
            Description = "Whether bricks snap to the edges of other bricks (as opposed to only snapping to walls).")]
        [ConfigHelp<int>("Brick", "BrickSpan", ConfigScope.Arena, Default = 10,
            Description = "The maximum length of a dropped brick.")]
        [ConfigHelp<BrickMode>("Brick", "BrickMode", ConfigScope.Arena, Default = BrickMode.Lateral,
            Description = """
                How bricks behave when they are dropped:
                  VIE = improved VIE style
                  AHEAD = drop in a line ahead of player
                  LATERAL = drop laterally across player
                  CAGE = drop 4 bricks simultaneously to create a cage
                """)]
        [ConfigHelp<int>("Brick", "BrickTime", ConfigScope.Arena, Default = 6000,
            Description = "How long bricks last (in ticks).")]
        [ConfigHelp<int>("Routing", "WallResendCount", ConfigScope.Arena, Default = 0, Min = 0, Max = 3,
            Description = "# of times a brick packet is sent unreliably, in addition to the reliable send.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                return;

            if (action == ArenaAction.Create)
            {
                abd.CurrentBrickId = 0;
                abd.LastTime = ServerTick.Now;
            }

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ConfigHandle ch = arena.Cfg!;
                abd.CountBricksAsWalls = _configManager.GetBool(ch, "Brick", "CountBricksAsWalls", BrickSettings.CountBricksAsWalls.Default);
                abd.BrickSpan = _configManager.GetInt(ch, "Brick", "BrickSpan", BrickSettings.BrickSpan.Default);
                abd.BrickMode = _configManager.GetEnum(ch, "Brick", "BrickMode", BrickMode.Lateral);
                abd.BrickTime = (uint)_configManager.GetInt(ch, "Brick", "BrickTime", BrickSettings.BrickTime.Default);
                abd.WallResendCount = Math.Clamp(_configManager.GetInt(ch, "Routing", "WallResendCount", RoutingSettings.WallResendCount.Default), RoutingSettings.WallResendCount.Min, RoutingSettings.WallResendCount.Max);

                // TODO: Add AntiBrickWarpDistance logic
            }
            else if (action == ArenaAction.Destroy)
            {
                abd.Bricks.Clear();
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterGame)
            {
                if (!arena!.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                    return;

                lock (abd.Lock)
                {
                    ExpireBricks(player.Arena!);

                    // Send active bricks to the player.
                    SendToPlayer(player, abd.Bricks, 0);
                }
            }
        }

        private void Callback_DoBrickMode(Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (length < 1)
                return;

            if (bricks is null)
                return;

            if (_mapData.GetTile(arena, new TileCoordinates(x, y), true) != MapTile.None)
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
                TileCoordinates start = new(x, y);
                TileCoordinates end = new(x, y);

                bool isStartDone = false;
                bool isEndDone = false;
                int tileCount = 1;

                if (direction == BrickDirection.Vertical)
                {
                    while ((!isStartDone || !isEndDone) && tileCount < length)
                    {
                        if (!isStartDone && start.Y > 0)
                        {
                            TileCoordinates newStart = new(start.X, (short)(start.Y - 1));
                            if (_mapData.GetTile(arena, newStart, true) == MapTile.None)
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
                            TileCoordinates newEnd = new(end.X, (short)(end.Y + 1));
                            if (_mapData.GetTile(arena, newEnd, true) == MapTile.None)
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
                            TileCoordinates newStart = new((short)(start.X - 1), start.Y);
                            if (_mapData.GetTile(arena, newStart, true) == MapTile.None)
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
                            TileCoordinates newEnd = new((short)(end.X + 1), end.Y);
                            if (_mapData.GetTile(arena, newEnd, true) == MapTile.None)
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

        private void Packet_Brick(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            Arena? arena = player.Arena;

            if (data.Length != C2S_Brick.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Bricks), player, $"Bad packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing || player.Ship == ShipType.Spec || arena is null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Bricks), player, "Ignored request from bad state.");
                return;
            }

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                return;

            ref readonly C2S_Brick c2sBrick = ref MemoryMarshal.AsRef<C2S_Brick>(data);

            ExpireBricks(arena);

            IBrickHandler? brickHandler = _broker.GetInterface<IBrickHandler>();
            if (brickHandler is null)
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

                ExpireBricks(arena);
                DropBricks(arena, freq, null, brickList);
            }
            finally
            {
                _brickLocationListPool.Return(brickList);
            }
        }

        private void DropBricks(Arena arena, short freq, Player? player, List<BrickLocation> bricks)
        {
            if (arena is null)
                return;

            if (bricks is null || bricks.Count <= 0)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                return;

            lock (abd.Lock)
            {
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

                        if (abd.CountBricksAsWalls)
                            _mapData.TryAddBrick(arena, brickData.BrickId, new TileCoordinates(brickData.X1, brickData.Y1), new TileCoordinates(brickData.X2, brickData.Y2));
                    }

                    _playerData.Lock();
                    try
                    {
                        foreach (Player otherPlayer in _playerData.Players)
                        {
                            if (otherPlayer.Arena != arena)
                                continue;

                            SendToPlayer(otherPlayer, brickDataList, abd.WallResendCount);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

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
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                return;

            ServerTick now = ServerTick.Now;

            lock (abd.Lock)
            {
                while (abd.Bricks.TryPeek(out BrickData brick)
                    && now >= brick.StartTime + abd.BrickTime)
                {
                    if (abd.CountBricksAsWalls)
                        _mapData.TryRemoveBrick(arena, brick.BrickId);

                    abd.Bricks.Dequeue();
                }
            }
        }

        private void SendToPlayer<T>(Player player, T bricks, int wallResendCount) where T : IReadOnlyCollection<BrickData>
        {
            if (player is null)
                return;

            if (bricks is null || bricks.Count <= 0)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            var advisors = arena.GetAdvisors<IBricksAdvisor>();

            //
            // create and send packet(s)
            //

            Span<byte> packetSpan = stackalloc byte[1 + (Math.Clamp(bricks.Count, 1, MaxBricksPerPacket) * BrickData.Length)];
            packetSpan[0] = (byte)S2CPacketType.Brick;

            Span<BrickData> brickSpan = MemoryMarshal.Cast<byte, BrickData>(packetSpan[1..]);
            int index = 0;

            foreach (BrickData brick in bricks)
            {
                if (!IsValidForPlayer(player, advisors, in brick))
                    continue;

                brickSpan[index++] = brick;

                if (index >= brickSpan.Length)
                {
                    // we have the maximum # of bricks that can be sent in a packet, send it
                    SendToPlayer(player, packetSpan, wallResendCount);
                    index = 0;
                }
            }

            if (index > 0)
            {
                SendToPlayer(player, packetSpan[..(1 + (index * BrickData.Length))], wallResendCount);
            }

            void SendToPlayer(Player player, Span<byte> data, int wallResendCount)
            {
                if (player is null)
                    return;

                if (data.Length <= 0)
                    return;

                // send it unreliably, urgently, and allow it to be dropped (as many times as is configured)
                for (int i = 0; i < wallResendCount; i++)
                {
                    _network.SendToOne(player, data, NetSendFlags.Unreliable | NetSendFlags.Droppable | NetSendFlags.PriorityP5); // NOTE: PriorityP5 has the Urgent flag set.
                }

                // send it reliably (always)
                _network.SendToOne(player, data, NetSendFlags.Reliable);
            }

            static bool IsValidForPlayer(Player player, ImmutableArray<IBricksAdvisor> advisors, ref readonly BrickData brick)
            {
                if (!advisors.IsEmpty)
                {
                    foreach (var advisor in advisors)
                    {
                        if (!advisor.IsValidForPlayer(player, in brick))
                        {
                            // Not valid if any advisor says so.
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        // Default implementation of IBrickHandler which fires the DoBrickModeCallback.
        // The callback provides a way for other modules to add new brick modes, without affecting existing ones.
        // It is a cleaner alternative than than having to override IBrickHandler.
        // NOTE: compared to ASSS, the callback passes Player instead of Arena.
        // This means more information about the player can be accessed (including rotation, velocity, etc.).
        // Of course, the arena can be accessed through the player too.
        void IBrickHandler.HandleBrick(Player player, short x, short y, IList<BrickLocation> bricks)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaBrickData? abd))
                return;

            DoBrickModeCallback.Fire(arena, player, abd.BrickMode, x, y, abd.BrickSpan, bricks);
        }

        #region Helper types

        public class ArenaBrickData : IResettable
        {
            /// <summary>
            /// Whether bricks snap to the edges of other bricks (as opposed to only snapping to walls).
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

            public readonly Lock Lock = new();

            public bool TryReset()
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

                return true;
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

        #endregion
    }
}
