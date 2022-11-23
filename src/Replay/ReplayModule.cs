using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Replay.FileFormat;
using SS.Replay.FileFormat.Events;
using SS.Utilities;
using SS.Utilities.Binary;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Replay
{
    /// <summary>
    /// A module that provides functionality to record and playback in-game replays.
    /// </summary>
    /// <remarks>
    /// This is based on the ASSS 'record' module.
    /// It uses the same file format (with additions), and intends to stay compatible with replays recorded by ASSS.
    /// 
    /// The implementation of this differs from ASSS in that it:
    /// - Does all file operations (including opening of streams) on a worker thread.
    /// - The playback thread queues up mainloop workitems.
    /// 
    /// This implementation adds the following functionality which is not included in ASSS:
    /// - balls (based on the PowerBall Zone fork of the 'record' module)
    /// - bricks (based on the PowerBall Zone fork of the 'record' module)
    /// - flags (both static flags and carryable flags)
    /// - crowns
    /// - door & green seeds (door timings will be in sync, greens will gradually become in sync)
    ///
    /// Chat message functionality is also enhanced beyond that of ASSS.
    /// This module provides the ability to record and playback: public chat, public macro chat, spectator chat, team chat, and arena chat.
    /// There are config settings to toggle the recording and playback of each.
    /// 
    /// TODO:
    /// - Record existing bricks when a recording starts. Unfortunately, there is no way to clear bricks on a client though.
    ///   Add an event type to record multiple bricks (to correspond to a brick packet containing multiple bricks, rather than just one).
    /// 
    /// Other info (to keep in mind):
    /// ASSS does not record the Position packet "time" field. It actually stores playerId in it.
    /// When it plays back a recording, the packet will be processed based on the event header ticks, and it uses the current tick count as "time".
    /// This doesn't seem accurate. Is there a better way? 
    /// The Game module's position packet handler does something similiar when sending S2C position packets (rather than use the C2S postition packet "time").
    /// </remarks>
    public class ReplayModule : IModule, IFreqManagerEnforcerAdvisor
    {
        private const uint ReplayFileVersion = 2;
        private const uint MapChecksumKey = 0x46692018;
        private const int MaxRecordBuffer = 4096;

        private IArenaManager _arenaManager;
        private IBalls _balls;
        private IBrickManager _brickManager;
        private IChat _chat;
        private IClientSettings _clientSettings;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ICrowns _crowns;
        private IFake _fake;
        private IGame _game;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMapData _mapData;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private ISecuritySeedSync _securitySeedSync;

        private ArenaDataKey<ArenaData> _adKey;

        private static readonly ArrayPool<byte> _recordBufferPool = ArrayPool<byte>.Create();

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IBalls balls,
            IBrickManager brickManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            ICrowns crowns,
            IFake fake,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMapData mapData,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            ISecuritySeedSync securitySeedSync)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _brickManager = brickManager ?? throw new ArgumentNullException(nameof(brickManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _crowns = crowns ?? throw new ArgumentNullException(nameof(crowns));
            _fake = fake ?? throw new ArgumentNullException(nameof(fake));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _securitySeedSync = securitySeedSync ?? throw new ArgumentNullException(nameof(securitySeedSync));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _commandManager.AddCommand("replay", Command_replay);
            _commandManager.AddCommand("gamerecord", Command_replay);
            _commandManager.AddCommand("rec", Command_replay);

            _network.AddPacket(C2SPacketType.Position, Packet_Position);

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            SecuritySeedChangedCallback.Register(broker, Callback_SecuritySeedChanged);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            SecuritySeedChangedCallback.Unregister(broker, Callback_SecuritySeedChanged);

            _network.RemovePacket(C2SPacketType.Position, Packet_Position);

            _commandManager.RemoveCommand("replay", Command_replay);
            _commandManager.RemoveCommand("gamerecord", Command_replay);
            _commandManager.RemoveCommand("rec", Command_replay);

            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            errorMessage?.Append("Ships are disabled for playback.");
            return ShipMask.None;
        }

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder errorMessage)
        {
            Arena arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.Settings.PlaybackLockTeams)
            {
                errorMessage?.Append("Teams are locked for playback.");
                return false;
            }
            else
            {
                return true;
            }
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create)
            {
                ad.Settings = new(_configManager, arena.Cfg);

                ad.State = ReplayState.None;
            }
            else if (action == ArenaAction.Destroy)
            {
                if (ad.State == ReplayState.Recording)
                    StopRecording(arena);
                else if (ad.State == ReplayState.Playing)
                    StopPlayback(arena);
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == PlayerAction.EnterArena)
            {
                byte[] buffer = _recordBufferPool.Rent(Enter.Length);
                ref Enter enter = ref MemoryMarshal.AsRef<Enter>(buffer);
                enter = new(ServerTick.Now, (short)p.Id, p.Name, p.Squad, p.Ship, p.Freq);

                ad.RecorderQueue.Add(new RecordBuffer(buffer, Enter.Length));
            }
            else if (action == PlayerAction.LeaveArena)
            {
                byte[] buffer = _recordBufferPool.Rent(Leave.Length);
                ref Leave leave = ref MemoryMarshal.AsRef<Leave>(buffer);
                leave = new(ServerTick.Now, (short)p.Id);

                ad.RecorderQueue.Add(new RecordBuffer(buffer, Leave.Length));
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Debug.Assert(_mainloop.IsMainloop);

            Arena arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (newShip == oldShip)
            {
                byte[] buffer = _recordBufferPool.Rent(FreqChange.Length);
                ref FreqChange freqChange = ref MemoryMarshal.AsRef<FreqChange>(buffer);
                freqChange = new(ServerTick.Now, (short)player.Id, newFreq);

                ad.RecorderQueue.Add(new RecordBuffer(buffer, FreqChange.Length));
            }
            else
            {
                byte[] buffer = _recordBufferPool.Rent(ShipChange.Length);
                ref ShipChange shipChange = ref MemoryMarshal.AsRef<ShipChange>(buffer);
                shipChange = new(ServerTick.Now, (short)player.Id, newShip, newFreq);

                ad.RecorderQueue.Add(new RecordBuffer(buffer, ShipChange.Length));
            }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] buffer = _recordBufferPool.Rent(ShipChange.Length);
            ref Kill kill = ref MemoryMarshal.AsRef<Kill>(buffer);
            kill = new(ServerTick.Now, (short)killer.Id, (short)killed.Id, pts, flagCount);

            ad.RecorderQueue.Add(new RecordBuffer(buffer, Kill.Length));
        }

        private void Callback_ChatMessage(Arena arena, Player player, ChatMessageType type, ChatSound sound, Player toPlayer, short freq, ReadOnlySpan<char> message)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (   (type == ChatMessageType.Arena && ad.Settings.RecordArenaChat)
                || (type == ChatMessageType.Pub && ad.Settings.RecordPublicChat)
                || (type == ChatMessageType.PubMacro && ad.Settings.RecordPublicMacroChat)
                || (type == ChatMessageType.Freq && (freq == arena.SpecFreq ? ad.Settings.RecordSpecChat : ad.Settings.RecordTeamChat)))
            {
                int messageByteCount = StringUtils.DefaultEncoding.GetByteCount(message) + 1;
                byte[] buffer = _recordBufferPool.Rent(Chat.Length + messageByteCount);
                ref Chat chat = ref MemoryMarshal.AsRef<Chat>(buffer);
                short fromPlayerId = (short)(player != null ? player.Id : -1);
                chat = new(ServerTick.Now, fromPlayerId, type, sound, (ushort)messageByteCount);
                Span<byte> messageBytes = buffer.AsSpan(Chat.Length, messageByteCount);
                messageBytes.WriteNullTerminatedString(message);

                ad.RecorderQueue.Add(new RecordBuffer(buffer, Chat.Length + messageByteCount));
            }
        }

        private void Callback_BricksPlaced(Arena arena, IReadOnlyList<BrickData> bricks)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            // TODO: Add another type of brick event that supports multiple bricks
            for (int i = 0; i < bricks.Count; i++) // use indexing, not enumerator (to avoid the boxing allocation)
            {
                BrickData brickData = bricks[i];

                byte[] buffer = _recordBufferPool.Rent(Brick.Length);
                ref Brick brick = ref MemoryMarshal.AsRef<Brick>(buffer);
                brick = new(ServerTick.Now, in brickData);

                ad.RecorderQueue.Add(new(buffer, Brick.Length));
            }
        }

        private void Callback_BallPacketSent(Arena arena, in BallPacket ballPacket)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] buffer = _recordBufferPool.Rent(BallPacketWrapper.Length);
            ref BallPacketWrapper ballPacketWrapper = ref MemoryMarshal.AsRef<BallPacketWrapper>(buffer);
            ballPacketWrapper = new(ServerTick.Now, in ballPacket);

            ad.RecorderQueue.Add(new(buffer, BallPacketWrapper.Length));
        }

        private void Callback_CrownToggled(Player player, bool on)
        {
            Debug.Assert(_mainloop.IsMainloop);

            Arena arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] buffer = _recordBufferPool.Rent(CrownToggle.Length);
            ref CrownToggle crownToggle = ref MemoryMarshal.AsRef<CrownToggle>(buffer);
            crownToggle = new(ServerTick.Now, on, (short)player.Id);

            ad.RecorderQueue.Add(new RecordBuffer(buffer, CrownToggle.Length));
        }

        private void Callback_SecuritySeedChanged(uint greenSeed, uint doorSeed, uint timestamp)
        {
            Debug.Assert(_mainloop.IsMainloop);

            _arenaManager.Lock();

            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                        return;

                    if (ad.State == ReplayState.Recording && ad.RecorderQueue != null && !ad.RecorderQueue.IsAddingCompleted)
                    {
                        byte[] buffer = _recordBufferPool.Rent(SecuritySeedChange.Length);
                        ref SecuritySeedChange seedChange = ref MemoryMarshal.AsRef<SecuritySeedChange>(buffer);
                        seedChange = new(timestamp, greenSeed, doorSeed, 0);

                        ad.RecorderQueue.Add(new RecordBuffer(buffer, SecuritySeedChange.Length));
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }
        }

        private void Callback_FlagGameReset(Arena arena, short winnerFreq, int points)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            // static flags
            if (QueueStaticFlagFullUpdate(arena, ad))
                return;

            // carry flags
            ICarryFlagGame carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (carryFlagGame != null)
            {
                arena.ReleaseInterface(ref carryFlagGame);

                byte[] buffer = _recordBufferPool.Rent(CarryFlagGameReset.Length);
                ref CarryFlagGameReset gameReset = ref MemoryMarshal.AsRef<CarryFlagGameReset>(buffer);
                gameReset = new(ServerTick.Now, winnerFreq, points);
                ad.RecorderQueue.Add(new RecordBuffer(buffer, CarryFlagGameReset.Length));
                return;
            }

            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, "Flag game reset in the arena, but was unable record the event.");
        }

        private void Callback_StaticFlagClaimed(Arena arena, Player player, byte flagId, short oldFreq, short newFreq)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] buffer = _recordBufferPool.Rent(StaticFlagClaimed.Length);
            ref StaticFlagClaimed claimed = ref MemoryMarshal.AsRef<StaticFlagClaimed>(buffer);
            claimed = new(ServerTick.Now, flagId, (short)player.Id);
            ad.RecorderQueue.Add(new RecordBuffer(buffer, StaticFlagClaimed.Length));
        }

        private void Callback_CarryFlagOnMap(Arena arena, short flagId, MapCoordinate mapCoordinate, short freq)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            QueueCarryFlagOnMap(ad, flagId, mapCoordinate, freq);
        }

        private void Callback_CarryFlagPickup(Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            QueueCarryFlagPickup(ad, flagId, player);
        }

        private void Callback_CarryFlagDrop(Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] buffer = _recordBufferPool.Rent(CarryFlagDrop.Length);
            ref CarryFlagDrop drop = ref MemoryMarshal.AsRef<CarryFlagDrop>(buffer);
            drop = new(ServerTick.Now, (short)player.Id);
            ad.RecorderQueue.Add(new RecordBuffer(buffer, CarryFlagDrop.Length));
        }

        #endregion

        private void Packet_Position(Player player, byte[] data, int length)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (length != C2S_PositionPacket.Length && length != C2S_PositionPacket.LengthWithExtra)
                return;

            Arena arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                if (ad.State != ReplayState.Recording)
                    return;
            }

            if (ad.RecorderQueue.IsAddingCompleted) // only the mainloop thread changes this, so it can't happen between here the Add method call since this is the mainloop
                return;

            ref C2S_PositionPacket c2sPosition = ref MemoryMarshal.AsRef<C2S_PositionPacket>(data);

            byte[] buffer = _recordBufferPool.Rent(Position.Length);
            ref Position position = ref MemoryMarshal.AsRef<Position>(buffer);
            position = new(ServerTick.Now, c2sPosition);
            position.PositionPacket.Type = (byte)length;
            position.PositionPacket.Time = (uint)player.Id;

            ad.RecorderQueue.Add(new RecordBuffer(buffer, EventHeader.Length + length));
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "status | record <file> | play <file> | pause | stop",
            Description = "Controls a replay recording or playback.")]
        private void Command_replay(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Debug.Assert(_mainloop.IsMainloop);

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token = remaining.GetToken(' ', out remaining);

            if (MemoryExtensions.Equals(token, "record", StringComparison.OrdinalIgnoreCase))
            {
                token = remaining.GetToken(' ', out remaining);
                if (token.IsWhiteSpace())
                {
                    _chat.SendMessage(player, $"Replay: A filename is required to record to.");
                    return;
                }

                if (!StartRecording(arena, token.ToString(), player, null))
                {
                    _chat.SendMessage(player, $"Replay: A recording cannot be started at this time.");
                }
            }
            else if (MemoryExtensions.Equals(token, "play", StringComparison.OrdinalIgnoreCase))
            {
                token = remaining.GetToken(' ', out remaining);
                if (token.IsWhiteSpace())
                {
                    _chat.SendMessage(player, $"Replay: A filename is required to play from.");
                    return;
                }

                if (!StartPlayback(arena, token.ToString(), player))
                {
                    _chat.SendMessage(player, $"Replay: A playback cannot be started at this time.");
                }
            }
            else if (MemoryExtensions.Equals(token, "stop", StringComparison.OrdinalIgnoreCase))
            {
                lock (ad.Lock)
                {
                    if (ad.State == ReplayState.Playing)
                        StopPlayback(arena);
                    else if (ad.State == ReplayState.Recording)
                        StopRecording(arena);
                }
            }
            else if (MemoryExtensions.Equals(token, "pause", StringComparison.OrdinalIgnoreCase))
            {
                bool success = false;

                lock (ad.Lock)
                {
                    if (ad.State == ReplayState.Playing)
                    {
                        if (ad.IsPlaybackPaused)
                            ad.PlaybackQueue.Add(PlaybackCommand.Resume);
                        else
                            ad.PlaybackQueue.Add(PlaybackCommand.Pause);

                        success = true;
                    }
                }

                if (!success)
                {
                    _chat.SendMessage(player, "Replay: Nothing is being played.");
                }
            }
            else
            {
                ReplayState state;
                string fileName = null;
                double playbackPosition = 0;
                bool isPlaybackPaused = false;

                lock (ad.Lock)
                {
                    state = ad.State;
                    if (state == ReplayState.Recording || state == ReplayState.Playing)
                    {
                        fileName = ad.FileName;
                    }

                    if (state == ReplayState.Playing)
                    {
                        playbackPosition = ad.PlaybackPosition;
                        isPlaybackPaused = ad.IsPlaybackPaused;
                    }
                }

                switch (state)
                {
                    case ReplayState.None:
                        _chat.SendMessage(player, "Replay: Nothing is being played or recorded.");
                        break;

                    case ReplayState.Recording:
                        if (fileName == null)
                        {
                            _chat.SendMessage(player, $"Replay: A recording is starting up. Please stand by.");
                        }
                        else
                        {
                            _chat.SendMessage(player, $"Replay: A replay is being recorded to '{fileName}'.");
                        }

                        break;

                    case ReplayState.Playing:
                        if (fileName == null)
                        {
                            _chat.SendMessage(player, $"Replay: A playback is starting up. Please stand by.");
                        }
                        else
                        {
                            _chat.SendMessage(player, $"Replay: A replay is being played from '{fileName}', current position {playbackPosition:P}{(isPlaybackPaused ? " (paused)" : "")}.");
                        }
                        break;

                    default:
                        _chat.SendMessage(player, $"Replay: The {nameof(ReplayModule)} module is in an invalid state.");
                        break;
                }
            }
        }

        private bool StartRecording(Arena arena, string path, Player recorder, string comments)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (ad.State != ReplayState.None || ad.RecorderQueue != null)
                    return false;

                if (ad.RecorderTask != null && !ad.RecorderTask.IsCompleted)
                    return false;

                ad.State = ReplayState.Recording;
                ad.StartedBy = recorder?.Name;
                ad.RecorderQueue = new();

                ServerTick started = ServerTick.Now;

                //
                // queue up starting info
                //

                // - security info (door/green seeds, and timestamp)
                QueueSecurityInfo(ad);

                // - players in the arena (enter events)
                QueuePlayers(arena, ad);

                // - crown state (which players have a crown)
                QueueCrownInfo(arena, ad);

                // - active bricks
                //QueueBricks(arena, ad);

                // - ball info
                //QueueBallInfo(arena, ad);

                // - flags
                QueueFlagInfo(arena, ad);

                PlayerActionCallback.Register(arena, Callback_PlayerAction);
                ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
                KillCallback.Register(arena, Callback_Kill);
                ChatMessageCallback.Register(arena, Callback_ChatMessage);
                BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
                BallPacketSentCallback.Register(arena, Callback_BallPacketSent);
                CrownToggledCallback.Register(arena, Callback_CrownToggled);
                FlagGameResetCallback.Register(arena, Callback_FlagGameReset);
                StaticFlagClaimedCallback.Register(arena, Callback_StaticFlagClaimed);
                FlagOnMapCallback.Register(arena, Callback_CarryFlagOnMap);
                FlagGainCallback.Register(arena, Callback_CarryFlagPickup);
                FlagLostCallback.Register(arena, Callback_CarryFlagDrop);

                ad.RecorderTask = Task.Factory.StartNew(() =>
                {
                    DoRecording(arena, path, started, comments);
                }, TaskCreationOptions.LongRunning).ContinueWith((_) =>
                {
                    _mainloop.QueueMainWorkItem(MainloopWorkItem_EndRecording, arena);
                });

                return true;
            }

            void QueueSecurityInfo(ArenaData ad)
            {
                _securitySeedSync.GetCurrentSeedInfo(out uint greenSeed, out uint doorSeed, out uint timestamp);
                byte[] buffer = _recordBufferPool.Rent(SecuritySeedChange.Length);
                ref SecuritySeedChange change = ref MemoryMarshal.AsRef<SecuritySeedChange>(buffer);
                ServerTick now = ServerTick.Now;
                change = new(now, greenSeed, doorSeed, (uint)(now - (ServerTick)timestamp));

                ad.RecorderQueue.Add(new RecordBuffer(buffer, SecuritySeedChange.Length));
            }

            void QueuePlayers(Arena arena, ArenaData ad)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Status == PlayerState.Playing)
                        {
                            byte[] buffer = _recordBufferPool.Rent(Enter.Length);
                            ref Enter enter = ref MemoryMarshal.AsRef<Enter>(buffer);
                            enter = new(ServerTick.Now, (short)player.Id, player.Name, player.Squad, player.Ship, player.Freq);

                            ad.RecorderQueue.Add(new RecordBuffer(buffer, Enter.Length));
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            void QueueCrownInfo(Arena arena, ArenaData ad)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Status == PlayerState.Playing
                            && player.Packet.HasCrown)
                        {
                            byte[] buffer = _recordBufferPool.Rent(CrownToggle.Length);
                            ref CrownToggle crownToggle = ref MemoryMarshal.AsRef<CrownToggle>(buffer);
                            crownToggle = new(ServerTick.Now, true, (short)player.Id);

                            ad.RecorderQueue.Add(new RecordBuffer(buffer, CrownToggle.Length));
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            //void QueueBricks(Arena arena, ArenaData ad)
            //{
                // TODO: this will require changes to the Bricks module:
                // - a way to get the current bricks
                // - a callback to record when brick(s) are placed
                // - a way to set bricks (with earlier timestamps) --> playback
            //}

            void QueueFlagInfo(Arena arena, ArenaData ad)
            {
                // static flags
                if (QueueStaticFlagFullUpdate(arena, ad))
                    return;

                // carry flags
                ICarryFlagGame carryFlagGame = arena.GetInterface<ICarryFlagGame>();
                if (carryFlagGame != null)
                {
                    try
                    {
                        short flagCount = carryFlagGame.GetFlagCount(arena);
                        if (flagCount <= 0)
                            return;

                        for (short flagId = 0; flagId < flagCount; flagId++)
                        {
                            if (!carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo flagInfo))
                                continue;

                            switch (flagInfo.State)
                            {
                                case FlagState.OnMap:
                                    QueueCarryFlagOnMap(ad, flagId, flagInfo.Location.Value, flagInfo.Freq);
                                    break;

                                case FlagState.Carried:
                                    QueueCarryFlagPickup(ad, flagId, flagInfo.Carrier);
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref carryFlagGame);
                    }
                }
            }
        }

        private bool QueueStaticFlagFullUpdate(Arena arena, ArenaData ad)
        {
            IStaticFlagGame staticFlagGame = arena.GetInterface<IStaticFlagGame>();
            if (staticFlagGame == null)
                return false;

            try
            {
                short flagCount = staticFlagGame.GetFlagCount(arena);
                if (flagCount > 0)
                {
                    Span<short> owners = stackalloc short[flagCount];
                    if (staticFlagGame.TryGetFlagOwners(arena, owners))
                    {
                        int ownersLength = flagCount * 2; // freq is an Int16
                        int eventLength = StaticFlagFullUpdate.Length + ownersLength;
                        byte[] buffer = _recordBufferPool.Rent(eventLength);
                        ref StaticFlagFullUpdate fullUpdate = ref MemoryMarshal.AsRef<StaticFlagFullUpdate>(buffer);
                        fullUpdate = new(ServerTick.Now, flagCount);
                        Span<Int16LittleEndian> fullUpdateOwners = MemoryMarshal.Cast<byte, Int16LittleEndian>(buffer.AsSpan(StaticFlagFullUpdate.Length, ownersLength));
                        for (int i = 0; i < flagCount; i++)
                        {
                            fullUpdateOwners[i] = owners[i];
                        }

                        ad.RecorderQueue.Add(new RecordBuffer(buffer, eventLength));
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                arena.ReleaseInterface(ref staticFlagGame);
            }
        }

        private static void QueueCarryFlagOnMap(ArenaData ad, short flagId, MapCoordinate location, short freq)
        {
            byte[] buffer = _recordBufferPool.Rent(CarryFlagOnMap.Length);
            ref CarryFlagOnMap onMap = ref MemoryMarshal.AsRef<CarryFlagOnMap>(buffer);
            onMap = new(ServerTick.Now, flagId, location.X, location.Y, freq);
            ad.RecorderQueue.Add(new RecordBuffer(buffer, CarryFlagOnMap.Length));
        }

        private static void QueueCarryFlagPickup(ArenaData ad, short flagId, Player player)
        {
            byte[] buffer = _recordBufferPool.Rent(CarryFlagPickup.Length);
            ref CarryFlagPickup pickup = ref MemoryMarshal.AsRef<CarryFlagPickup>(buffer);
            pickup = new(ServerTick.Now, flagId, (short)player.Id);
            ad.RecorderQueue.Add(new RecordBuffer(buffer, CarryFlagPickup.Length));
        }

        private bool StopRecording(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (ad.State != ReplayState.Recording || ad.RecorderQueue == null || ad.RecorderQueue.IsAddingCompleted)
                    return false;

                PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
                ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
                KillCallback.Unregister(arena, Callback_Kill);
                ChatMessageCallback.Unregister(arena, Callback_ChatMessage);
                BricksPlacedCallback.Unregister(arena, Callback_BricksPlaced);
                BallPacketSentCallback.Unregister(arena, Callback_BallPacketSent);
                CrownToggledCallback.Unregister(arena, Callback_CrownToggled);
                FlagGameResetCallback.Unregister(arena, Callback_FlagGameReset);
                StaticFlagClaimedCallback.Unregister(arena, Callback_StaticFlagClaimed);
                FlagOnMapCallback.Unregister(arena, Callback_CarryFlagOnMap);
                FlagGainCallback.Unregister(arena, Callback_CarryFlagPickup);
                FlagLostCallback.Unregister(arena, Callback_CarryFlagDrop);

                ad.RecorderQueue.CompleteAdding();
                return true;
            }
        }

        private void DoRecording(Arena arena, string path, ServerTick started, string comments)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine("recordings", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{arena.Name}");
            }
            else if (!path.StartsWith("recordings", StringComparison.OrdinalIgnoreCase)
                || path.Length <= ("recordings".Length + 1)
                || (path["recordings".Length] != Path.DirectorySeparatorChar && path["recordings".Length] != Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine("recordings", path);
            }

            // Create the file.
            FileStream fileStream;

            try
            {
                fileStream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                LogAndNotify(arena, ad.Settings.NotifyRecordingError, $"Unable to create replay file '{path}'.", ex);
                return;
            }

            Notify(arena, ad.Settings.NotifyRecording, $"Started recording '{path}'.");

            try
            {
                BlockingCollection<RecordBuffer> recorderQueue;

                lock (ad.Lock)
                {
                    recorderQueue = ad.RecorderQueue ?? throw new Exception("The recorder queue was null.");
                    ad.FileName = path;
                }

                int commentsLength = comments != null ? StringUtils.DefaultEncoding.GetByteCount(comments) + 1 : 0;

                // Write the file header (though only have partial data and will have to update it at the end).
                FileHeader fileHeader = new()
                {
                    Header = "ass$game", // The $ temporary, we will update it at the end.
                    Version = ReplayFileVersion,
                    Offset = (uint)FileHeader.Length + (uint)commentsLength,
                    Events = 0, // This will be updated at the end.
                    EndTime = 0, // This will be updated at the end.
                    MaxPlayerId = 0, // This will be updated at the end.
                    SpecFreq = (uint)arena.SpecFreq,
                    Recorded = DateTimeOffset.UtcNow,
                    MapChecksum = _mapData.GetChecksum(arena, MapChecksumKey),
                    Recorder = ad.StartedBy,
                    ArenaName = arena.Name
                };

                Span<byte> fileHeaderBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref fileHeader, 1));

                try
                {
                    fileStream.Write(fileHeaderBytes);
                }
                catch (Exception ex)
                {
                    throw new Exception("Unable to write file header.", ex);
                }

                // Write the file header comments.
                if (commentsLength > 0)
                {
                    byte[] commentsBuffer = ArrayPool<byte>.Shared.Rent(commentsLength);
                    try
                    {
                        Span<byte> commentsSpan = commentsBuffer.AsSpan(0, commentsLength);
                        int numBytes = StringUtils.WriteNullTerminatedString(commentsSpan, comments);
                        if (numBytes != commentsLength)
                        {
                            throw new Exception($"Encoding resulted in {numBytes} bytes when {commentsLength} bytes were expected.");
                        }

                        fileStream.Write(commentsBuffer);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unable to write file header (comments).", ex);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(commentsBuffer, true);
                    }
                }

                // Open the gzip stream.
                using GZipStream gzStream = new(fileStream, CompressionLevel.Optimal);

                uint eventCount = 0;
                int maxPlayerId = 0;

                // Write events.
                while (!recorderQueue.IsCompleted)
                {
                    if (!recorderQueue.TryTake(out RecordBuffer recordBuffer, -1))
                        continue;

                    try
                    {
                        Span<byte> eventBytes = recordBuffer.Buffer.AsSpan(0, recordBuffer.Length);
                        ref EventHeader eventHeader = ref MemoryMarshal.AsRef<EventHeader>(eventBytes);

                        // Normalize events to start from 0.
                        eventHeader.Ticks = (ServerTick)(uint)(eventHeader.Ticks - started);

                        if (eventHeader.Type == EventType.Enter)
                        {
                            // Keep track of the maximum playerId so that it can be written to the header when the recording is complete.
                            ref Enter enter = ref MemoryMarshal.AsRef<Enter>(eventBytes);
                            if (enter.PlayerId > maxPlayerId)
                                maxPlayerId = enter.PlayerId;
                        }
                        else if (eventHeader.Type == EventType.Brick)
                        {
                            ref Brick brick = ref MemoryMarshal.AsRef<Brick>(eventBytes);
                            brick.BrickData.StartTime = (uint)(brick.BrickData.StartTime - started);
                        }
                        else if (eventHeader.Type == EventType.BallPacket)
                        {
                            ref BallPacketWrapper ballPacketWrapper = ref MemoryMarshal.AsRef<BallPacketWrapper>(eventBytes);
                            if (ballPacketWrapper.BallPacket.Time != 0)
                            {
                                ballPacketWrapper.BallPacket.Time = (uint)(ballPacketWrapper.BallPacket.Time - started);
                            }
                        }

                        // Write the event.
                        try
                        {
                            gzStream.Write(eventBytes);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Unable to write event of type {eventHeader.Type}.", ex);
                        }

                        eventCount++;
                    }
                    finally
                    {
                        _recordBufferPool.Return(recordBuffer.Buffer, true);
                    }
                }

                // Update the file header.
                fileHeader.Header = "asssgame";
                fileHeader.Events = eventCount;
                fileHeader.EndTime = (uint)(ServerTick.Now - started);
                fileHeader.MaxPlayerId = (uint)maxPlayerId;

                // Write the updated file header to the file.
                long originalPosition = fileStream.Position;
                fileStream.Position = 0;

                try
                {
                    fileStream.Write(fileHeaderBytes);
                }
                catch (Exception ex)
                {
                    throw new Exception("Unable to edit file header.", ex);
                }

                fileStream.Position = originalPosition;
            }
            catch (Exception ex)
            {
                LogAndNotify(arena, ad.Settings.NotifyRecordingError, $"Error recording to '{path}'.", ex);
            }
            finally
            {
                fileStream.Dispose();

                Notify(arena, ad.Settings.NotifyRecording, $"Stopped recording '{path}'.");
            }
        }

        /// <summary>
        /// Performs cleanup when a recording ends.
        /// </summary>
        /// <remarks>
        /// This is executed on the mainloop thread since the mainloop thread is the producer.
        /// It is important that it's done on the mainloop thread because the <see cref="Packet_Position"/> method is registered globally and occurs on the mainloop thread.
        /// </remarks>
        /// <param name="arena"></param>
        private void MainloopWorkItem_EndRecording(Arena arena)
        {
            Debug.Assert(_mainloop.IsMainloop);

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            // Make sure callbacks are unregistered and the queue is marked as complete for adding.
            // If the recording was told to stop, then this will have already been done and calling it again will do nothing.
            // If the recording errored out, then this will bring it into the state we're expecting.
            StopRecording(arena);

            lock (ad.Lock)
            {
                Debug.Assert(ad.RecorderTask.IsCompleted);

                ad.State = ReplayState.None;
                ad.FileName = null;
                ad.StartedBy = null;

                // If there are any remaining queued items, make sure to return their buffers to the pool.
                while (!ad.RecorderQueue.IsCompleted)
                {
                    if (!ad.RecorderQueue.TryTake(out RecordBuffer recordBuffer))
                        continue;

                    _recordBufferPool.Return(recordBuffer.Buffer, true);
                }

                ad.RecorderQueue.Dispose();
                ad.RecorderQueue = null;

                ad.RecorderTask = null;
            }
        }

        private bool StartPlayback(Arena arena, string path, Player startedBy)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (ad.State != ReplayState.None)
                    return false;

                if (ad.PlaybackTask != null && !ad.PlaybackTask.IsCompleted)
                    return false;

                ad.State = ReplayState.Playing;
                ad.StartedBy = startedBy?.Name;
                ad.PlaybackPosition = 0;
                ad.IsPlaybackPaused = false;

                // make sure the queue is empty before we start
                while (ad.PlaybackQueue.TryTake(out _)) { }

                // Tell the Balls module to remove any balls.
                // This prevents the Balls module from sending ball position update packets which would interfere with ball events in the replay.
                _balls.TrySetBallCount(arena, 0);

                ICarryFlagGame carryFlagGame = arena.GetInterface<ICarryFlagGame>();
                if (carryFlagGame != null)
                {
                    try
                    {
                        // Stop any existing carry flag game and DON'T allow it to automatically restart.
                        carryFlagGame.ResetGame(arena, -1, 0, false);
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref carryFlagGame);
                    }
                }

                ad.PlaybackTask = Task.Factory.StartNew(() =>
                {
                    DoPlayback(arena, path);
                }, TaskCreationOptions.LongRunning).ContinueWith((_) =>
                    _mainloop.QueueMainWorkItem(MainloopWorkitem_EndPlayback, arena)
                );

                return true;
            }
        }

        private bool StopPlayback(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            lock (ad.Lock)
            {
                if (ad.State != ReplayState.Playing)
                    return false;

                ad.PlaybackQueue.Add(PlaybackCommand.Stop);
                return true;
            }
        }

        private void DoPlayback(Arena arena, string path)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            else if (!path.StartsWith("recordings", StringComparison.OrdinalIgnoreCase)
                || path.Length <= ("recordings".Length + 1)
                || (path["recordings".Length] != Path.DirectorySeparatorChar && path["recordings".Length] != Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine("recordings", path);
            }

            // Try to open the file.
            FileStream fileStream;

            try
            {
                fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception ex)
            {
                LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"Unable to open replay file '{path}'.", ex);
                return;
            }

            try
            {
                lock (ad.Lock)
                {
                    ad.FileName = path;
                }

                // Try to read the file header.
                FileHeader fileHeader = new();

                try
                {
                    Span<byte> fileHeaderBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref fileHeader, 1));
                    if (ReadFromStream(fileStream, fileHeaderBytes) != fileHeaderBytes.Length)
                    {
                        LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"File is not a replay.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"Unable to read file header.", ex);
                    return;
                }

                ReadOnlySpan<byte> headerBytes = fileHeader.HeaderBytes.SliceNullTerminated();
                Span<char> headerChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(headerBytes)];
                int decodedBytes = StringUtils.DefaultEncoding.GetChars(headerBytes, headerChars);
                Debug.Assert(decodedBytes == headerBytes.Length);

                if (!MemoryExtensions.Equals(headerChars, "asssgame", StringComparison.Ordinal))
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"File is not a replay.");
                    return;
                }
                else if (fileHeader.Version != ReplayFileVersion)
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"Unsupported replay version.");
                    return;
                }
                else if (ad.Settings.PlaybackMapCheckEnabled
                    && fileHeader.MapChecksum != _mapData.GetChecksum(arena, MapChecksumKey))
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"The map in the arena does not match the replay's.");
                    return;
                }
                else if (ad.Settings.PlaybackSpecFreqCheckEnabled
                    && (short)fileHeader.SpecFreq != arena.SpecFreq)
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"The arena spec freq ({arena.SpecFreq}) does not match the replay's ({(short)fileHeader.SpecFreq}).");
                    return;
                }

                // Move to where the events begin.
                try
                {
                    fileStream.Seek(fileHeader.Offset, SeekOrigin.Begin);
                }
                catch (Exception ex)
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"Unable to seek to the beginning of the events.", ex);
                    return;
                }

                // The events are compressed with gzip. Initialize the stream to decompress and read from.
                GZipStream gzStream;

                try
                {
                    gzStream = new(fileStream, CompressionMode.Decompress, true);
                }
                catch (Exception ex)
                {
                    LogAndNotify(arena, ad.Settings.NotifyPlaybackError, $"Error opening gzip stream.", ex);
                    return;
                }

                try
                {
                    // Notify the arena that playback is starting.
                    if (ad.Settings.NotifyPlayback != NotifyOption.None)
                    {
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            sb.Append($"Starting playback of '{path}' recorded ");

                            ReadOnlySpan<byte> arenaNameBytes = fileHeader.ArenaNameBytes.SliceNullTerminated();
                            Span<char> arenaNameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(arenaNameBytes)];
                            decodedBytes = StringUtils.DefaultEncoding.GetChars(arenaNameBytes, arenaNameChars);
                            Debug.Assert(decodedBytes == arenaNameBytes.Length);

                            if (!MemoryExtensions.IsWhiteSpace(arenaNameChars))
                                sb.Append($"in arena {arenaNameChars} ");

                            ReadOnlySpan<byte> recorderBytes = fileHeader.RecorderBytes.SliceNullTerminated();
                            Span<char> recorderChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(recorderBytes)];
                            decodedBytes = StringUtils.DefaultEncoding.GetChars(recorderBytes, recorderChars);
                            Debug.Assert(decodedBytes == recorderBytes.Length);

                            if (!MemoryExtensions.IsWhiteSpace(recorderChars))
                                sb.Append($"by {recorderChars} ");

                            sb.Append($"on {fileHeader.Recorded}");

                            Notify(arena, ad.Settings.NotifyPlayback, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }

                    // Lock everyone watching to spectator mode.
                    LockAllSpec(arena); // TODO: does this need to be done on the mainloop? If so, then we'll need to wait for it to complete too.

                    Span<byte> buffer = stackalloc byte[MaxRecordBuffer]; // no event can be larger than this
                    buffer.Clear();

                    ref EventHeader head = ref MemoryMarshal.AsRef<EventHeader>(buffer);

                    // for reading variable length events
                    ref Chat chat = ref MemoryMarshal.AsRef<Chat>(buffer);
                    ref StaticFlagFullUpdate staticFlagFullUpdate = ref MemoryMarshal.AsRef<StaticFlagFullUpdate>(buffer);

                    ServerTick started = ServerTick.Now;
                    ServerTick? paused = null;

                    int readLength = 0;

                    while (true)
                    {
                        // Check if there is a command.
                        if (ad.PlaybackQueue.TryTake(out PlaybackCommand command, paused == null ? 0 : -1))
                        {
                            if (command == PlaybackCommand.Stop)
                            {
                                break;
                            }
                            else if (command == PlaybackCommand.Pause)
                            {
                                bool changed = false;

                                lock (ad.Lock)
                                {
                                    if (!ad.IsPlaybackPaused)
                                    {
                                        ad.IsPlaybackPaused = true;
                                        changed = true;
                                    }
                                }

                                if (changed)
                                {
                                    paused = ServerTick.Now;
                                    Notify(arena, ad.Settings.NotifyPlayback, "Playback paused.");
                                }

                                continue;
                            }
                            else if (command == PlaybackCommand.Resume)
                            {
                                bool changed = false;

                                lock (ad.Lock)
                                {
                                    if (ad.IsPlaybackPaused)
                                    {
                                        ad.IsPlaybackPaused = false;
                                        changed = true;
                                    }
                                }

                                if (changed)
                                {
                                    started += (uint)(ServerTick.Now - paused.Value);
                                    paused = null;
                                    Notify(arena, ad.Settings.NotifyPlayback, "Playback resumed.");
                                }
                            }
                        }

                        if (readLength == 0)
                        {
                            // Try to read the next event.

                            try
                            {
                                // Read the event header.
                                if ((readLength += ReadFromStream(gzStream, buffer[..EventHeader.Length])) != EventHeader.Length)
                                {
                                    // no more events
                                    return;
                                }

                                // Read the rest of the event.
                                switch (head.Type)
                                {
                                    case EventType.Null:
                                        break;

                                    case EventType.Enter:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..Enter.Length])) != Enter.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for an {EventType.Enter} event.");
                                            return;
                                        }

                                        break;

                                    case EventType.Leave:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..Leave.Length])) != Leave.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Leave} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.ShipChange:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..ShipChange.Length])) != ShipChange.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.ShipChange} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.FreqChange:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..FreqChange.Length])) != FreqChange.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.FreqChange} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.Kill:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..Kill.Length])) != Kill.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Kill} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.Chat:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..Chat.Length])) != Chat.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Chat} event.");
                                            return;
                                        }

                                        // The message comes next, but it is variable in length.
                                        if (chat.MessageLength < 1 || chat.MessageLength >= 512)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Bad message length in {EventType.Chat} event.");
                                            return;
                                        }

                                        if ((readLength += ReadFromStream(gzStream, buffer.Slice(Chat.Length, chat.MessageLength))) != Chat.Length + chat.MessageLength)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Chat} event message.");
                                            return;
                                        }

                                        break;

                                    case EventType.Position:
                                        // The position event is variable in length.
                                        // The first byte after the EventHeader (the Type field) actually holds the length.
                                        if ((readLength += ReadFromStream(gzStream, buffer.Slice(EventHeader.Length, 1))) != EventHeader.Length + 1)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Position} event.");
                                            return;
                                        }

                                        int length = buffer[EventHeader.Length];
                                        if (length != 22 && length != 24 && length != 32)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Bad length in {EventType.Position} event.");
                                            return;
                                        }

                                        if ((readLength += ReadFromStream(gzStream, buffer.Slice(EventHeader.Length + 1, length - 1))) != EventHeader.Length + length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Position} event.");
                                            return;
                                        }

                                        // Always send a buffer big enough to for a full length Position. This will make it easier to read.
                                        if (readLength < Position.Length)
                                        {
                                            int extra = Position.Length - readLength;
                                            buffer.Slice(EventHeader.Length + length, extra).Clear(); // zero the extra bytes to be nice
                                            readLength += extra;
                                        }

                                        break;

                                    case EventType.Packet:
                                        break; // TODO:

                                    case EventType.Brick:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..Brick.Length])) != Brick.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.Brick} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.BallFire:
                                    case EventType.BallCatch:
                                        break;

                                    case EventType.BallPacket:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..BallPacketWrapper.Length])) != BallPacketWrapper.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.BallPacket} event.");
                                            return;
                                        }

                                        ref BallPacketWrapper ballPacketWrapper = ref MemoryMarshal.AsRef<BallPacketWrapper>(buffer);
                                        if (ballPacketWrapper.BallPacket.Time != 0)
                                        {
                                            // Convert the normalized time to be relative to the start time.
                                            ballPacketWrapper.BallPacket.Time = started + ballPacketWrapper.BallPacket.Time;
                                        }

                                        break;

                                    case EventType.BallGoal:
                                    case EventType.ArenaMessage: // investigate why this was created? instead of using the chat event?
                                        break; // TODO:

                                    case EventType.CrownToggleOn:
                                    case EventType.CrownToggleOff:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..CrownToggle.Length])) != CrownToggle.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.StaticFlagFullUpdate:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..StaticFlagFullUpdate.Length])) != StaticFlagFullUpdate.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }

                                        short flagCount = staticFlagFullUpdate.FlagCount;
                                        int flagOwnerByteLength = flagCount * 2;
                                        if ((readLength += ReadFromStream(gzStream, buffer.Slice(StaticFlagFullUpdate.Length, flagOwnerByteLength))) != StaticFlagFullUpdate.Length + flagOwnerByteLength)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {EventType.StaticFlagFullUpdate} event with {flagCount} flags.");
                                            return;
                                        }

                                        break;

                                    case EventType.StaticFlagClaimed:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..StaticFlagClaimed.Length])) != StaticFlagClaimed.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.CarryFlagGameReset:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..CarryFlagGameReset.Length])) != CarryFlagGameReset.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.CarryFlagOnMap:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..CarryFlagOnMap.Length])) != CarryFlagOnMap.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.CarryFlagPickup:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..CarryFlagPickup.Length])) != CarryFlagPickup.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.CarryFlagDrop:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..CarryFlagDrop.Length])) != CarryFlagDrop.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    case EventType.SecuritySeedChange:
                                        if ((readLength += ReadFromStream(gzStream, buffer[EventHeader.Length..SecuritySeedChange.Length])) != SecuritySeedChange.Length)
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to read enough bytes for a {head.Type} event.");
                                            return;
                                        }
                                        break;

                                    default:
                                        _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unknown event type {head.Type}.");
                                        return;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogAndNotify(arena, ad.Settings.NotifyPlaybackError, "Unable to read event.", ex);
                                return;
                            }
                        }

                        // We have an event.
                        ServerTick now = ServerTick.Now;

                        lock (ad.Lock)
                        {
                            ad.PlaybackPosition = (now - started) / (double)fileHeader.EndTime;
                        }

                        // Check if it's time for the event to be processed.
                        if (now - started < head.Ticks)
                        {
                            // Not yet time to process the event.
                            // Normally, events will occur at a rate faster than the granularity of sleeping,
                            // at least on Windows, which is approximately 15.625 ms (1000 ms / 64).
                            // So, using a SpinWait since we expect to be doing lots of work pretty much continuously.
                            int waitMs = (int)(head.Ticks - (now - started)) * 10;
                            //_logManager.LogA(LogLevel.Drivel, nameof(ReplayModule), arena, $"ticks {head.Ticks} waiting for {waitMs} ms");
                            SpinWait.SpinUntil(() => ad.PlaybackQueue.Count > 0, waitMs);
                            continue;
                        }

                        // Queue up the event to be processed by the mainloop thread.
                        byte[] playbackBytes = _recordBufferPool.Rent(readLength);
                        buffer[..readLength].CopyTo(playbackBytes);

                        _mainloop.QueueMainWorkItem(
                            ProcessPlaybackEvent, // TODO: does this allocate a delegate once or once per call? might need to cache the delegate to reduce allocations
                            new PlaybackBuffer(arena, playbackBytes, readLength));

                        // Signal to read the next event.
                        readLength = 0;
                    }
                }
                finally
                {
                    gzStream.Dispose();
                    gzStream = null;

                    _mainloop.WaitForMainWorkItemDrain();

                    // Make sure all faked players leave.
                    foreach (Player player in ad.PlayerIdMap.Values)
                    {
                        _fake.EndFaked(player);
                    }

                    ad.PlayerIdMap.Clear();

                    _securitySeedSync.RemoveArenaOverride(arena);

                    // TODO: remove balls

                    Notify(arena, ad.Settings.NotifyPlayback, "Playback stopped.");

                    UnlockAllSpec(arena);
                }
            }
            finally
            {
                fileStream.Dispose();
                fileStream = null;
            }

            void ProcessPlaybackEvent(PlaybackBuffer playbackBuffer)
            {
                try
                {
                    Arena arena = playbackBuffer.Arena;
                    if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                        return;

                    Span<byte> buffer = playbackBuffer.Buffer.AsSpan(0, playbackBuffer.Length);
                    ref EventHeader head = ref MemoryMarshal.AsRef<EventHeader>(buffer);
                    ServerTick now = ServerTick.Now;
                    Player player;

                    switch (head.Type)
                    {
                        case EventType.Null:
                            break;

                        case EventType.Enter:
                            ref Enter enter = ref MemoryMarshal.AsRef<Enter>(buffer);

                            Span<byte> nameBytes = enter.NameBytes.SliceNullTerminated();
                            Span<char> name = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)+1];
                            name[0] = '~';
                            int decodedByteCount = StringUtils.DefaultEncoding.GetChars(nameBytes, name[1..]);
                            Debug.Assert(decodedByteCount == nameBytes.Length);

                            if (ad.PlayerIdMap.ContainsKey(enter.PlayerId))
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Duplicate Enter event for player {enter.PlayerId} '{name}'.");
                                break;
                            }

                            player = _fake.CreateFakePlayer(name, arena, enter.Ship, enter.Freq);
                            if (player != null)
                            {
                                ad.PlayerIdMap.Add(enter.PlayerId, player);
                            }
                            break;

                        case EventType.Leave:
                            ref Leave leave = ref MemoryMarshal.AsRef<Leave>(buffer);

                            if (ad.PlayerIdMap.Remove(leave.PlayerId, out player))
                            {
                                _fake.EndFaked(player);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Leave event for non-existent PlayerId {leave.PlayerId}.");
                            }
                            break;

                        case EventType.ShipChange:
                            ref ShipChange shipChange = ref MemoryMarshal.AsRef<ShipChange>(buffer);

                            if (ad.PlayerIdMap.TryGetValue(shipChange.PlayerId, out player))
                            {
                                _game.SetShipAndFreq(player, shipChange.NewShip, shipChange.NewFreq);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"ShipChange event for non-existent PlayerId {shipChange.PlayerId}.");
                            }
                            break;

                        case EventType.FreqChange:
                            ref FreqChange freqChange = ref MemoryMarshal.AsRef<FreqChange>(buffer);

                            if (ad.PlayerIdMap.TryGetValue(freqChange.PlayerId, out player))
                            {
                                _game.SetFreq(player, freqChange.NewFreq);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"FreqChange event for non-existent PlayerId {freqChange.PlayerId}.");
                            }
                            break;

                        case EventType.Kill:
                            ref Kill kill = ref MemoryMarshal.AsRef<Kill>(buffer);

                            if (!ad.PlayerIdMap.TryGetValue(kill.Killer, out Player killer))
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Kill event for non-existent killer PlayerId {kill.Killer}.");
                            }
                            else if (!ad.PlayerIdMap.TryGetValue(kill.Killed, out Player killed))
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Kill event for non-existent killed PlayerId {kill.Killed}.");
                            }
                            else
                            {
                                _game.FakeKill(killer, killed, kill.Points, kill.Flags);
                            }
                            break;

                        case EventType.Chat:
                            ref Chat chat = ref MemoryMarshal.AsRef<Chat>(buffer);

                            Span<byte> messageBytes = buffer.Slice(Chat.Length, chat.MessageLength);
                            messageBytes = StringUtils.SliceNullTerminated(messageBytes);
                            int numChars = StringUtils.DefaultEncoding.GetCharCount(messageBytes);
                            Span<char> messageChars = stackalloc char[numChars];

                            if (StringUtils.DefaultEncoding.GetChars(messageBytes, messageChars) != numChars)
                                return;

                            if (chat.Type == ChatMessageType.Arena && ad.Settings.PlaybackArenaChat)
                            {
                                _chat.SendArenaMessage(arena, chat.Sound, messageChars);
                            }
                            else
                            {
                                if (!ad.PlayerIdMap.TryGetValue(chat.PlayerId, out player))
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Chat event for non-existent PlayerId {chat.PlayerId}.");
                                    return;
                                }

                                if (   (chat.Type == ChatMessageType.Pub && ad.Settings.PlaybackPublicChat)
                                    || (chat.Type == ChatMessageType.PubMacro && ad.Settings.PlaybackPublicMacroChat)
                                    || (chat.Type == ChatMessageType.Freq && (player.Freq == arena.SpecFreq ? ad.Settings.PlaybackSpecChat : ad.Settings.PlaybackTeamChat)))
                                {
                                    short? freq = chat.Type == ChatMessageType.Freq ? player.Freq : null;

                                    HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                                    try
                                    {
                                        GetWatching(arena, players, freq);
                                        _chat.SendAnyMessage(players, chat.Type, chat.Sound, player, messageChars);
                                    }
                                    finally
                                    {
                                        _objectPoolManager.PlayerSetPool.Return(players);
                                    }
                                }
                            }
                            break;

                        case EventType.Position:
                            ref Position position = ref MemoryMarshal.AsRef<Position>(buffer);

                            int length = position.PositionPacket.Type;
                            short playerId = (short)(uint)position.PositionPacket.Time;
                            if (ad.PlayerIdMap.TryGetValue(playerId, out player))
                            {
                                position.PositionPacket.Type = (byte)C2SPacketType.Position;
                                position.PositionPacket.Time = now; // This is not entirely accurate!
                                _game.FakePosition(player, ref position.PositionPacket, length);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Position event for non-existent PlayerId {playerId}");
                            }
                            break;

                        case EventType.Packet:
                            ref PacketWrapper packetWrapper = ref MemoryMarshal.AsRef<PacketWrapper>(buffer);
                            break;

                        case EventType.Brick:
                            ref Brick brick = ref MemoryMarshal.AsRef<Brick>(buffer);
                            _brickManager.DropBrick(arena, brick.BrickData.Freq, brick.BrickData.X1, brick.BrickData.Y1, brick.BrickData.X2, brick.BrickData.Y2);
                            break;

                        case EventType.BallFire:
                            break;

                        case EventType.BallCatch:
                            break;

                        case EventType.BallPacket:
                            {
                                ref BallPacketWrapper ballPacketWrapper = ref MemoryMarshal.AsRef<BallPacketWrapper>(buffer);

                                if (ballPacketWrapper.BallPacket.PlayerId != -1)
                                {
                                    if (!ad.PlayerIdMap.TryGetValue(ballPacketWrapper.BallPacket.PlayerId, out player))
                                    {
                                        _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{EventType.BallPacket} event for non-existent carrier, PlayerId {ballPacketWrapper.BallPacket.PlayerId}");
                                        return;
                                    }

                                    ballPacketWrapper.BallPacket.PlayerId = (short)player.Id;
                                }

                                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                                try
                                {
                                    GetWatching(arena, players, null);
                                    _network.SendToSet(players, ref ballPacketWrapper.BallPacket, NetSendFlags.Unreliable | NetSendFlags.PriorityP4);
                                }
                                finally
                                {
                                    _objectPoolManager.PlayerSetPool.Return(players);
                                }
                                break;
                            }
                        case EventType.BallGoal:
                            break;

                        case EventType.ArenaMessage:
                            break;

                        case EventType.CrownToggleOn:
                        case EventType.CrownToggleOff:
                            {
                                bool on = head.Type == EventType.CrownToggleOn;
                                ref CrownToggle crownToggle = ref MemoryMarshal.AsRef<CrownToggle>(buffer);

                                if (ad.PlayerIdMap.TryGetValue(crownToggle.PlayerId, out player))
                                {
                                    if (head.Type == EventType.CrownToggleOn)
                                        _crowns.ToggleOn(player, TimeSpan.Zero);
                                    else
                                        _crowns.ToggleOff(player);
                                }
                                else
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{head.Type} event for non-existent PlayerId {crownToggle.PlayerId}.");
                                }
                            }
                            break;

                        case EventType.StaticFlagFullUpdate:
                            {
                                ref StaticFlagFullUpdate fullUpdate = ref MemoryMarshal.AsRef<StaticFlagFullUpdate>(buffer);
                                short flagCount = fullUpdate.FlagCount;
                                Span<byte> flagOwnerBytes = buffer[StaticFlagFullUpdate.Length..];
                                Span<short> flagOwners = MemoryMarshal.Cast<byte, short>(flagOwnerBytes);
                                if (flagOwners.Length != flagCount)
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to playback the {EventType.StaticFlagFullUpdate} because the event's data is not the right length for the flag count it specifies.");
                                    return;
                                }

                                if (!BitConverter.IsLittleEndian)
                                {
                                    for (int i = 0; i < flagCount; i++)
                                    {
                                        flagOwners[i] = BinaryPrimitives.ReverseEndianness(flagOwners[i]);
                                    }
                                }

                                IStaticFlagGame staticFlagGame = arena.GetInterface<IStaticFlagGame>();
                                if (staticFlagGame != null)
                                {
                                    try
                                    {
                                        if (!staticFlagGame.SetFlagOwners(arena, flagOwners))
                                        {
                                            _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{EventType.StaticFlagFullUpdate} event, but setting flag owners failed.");
                                        }
                                    }
                                    finally
                                    {
                                        arena.ReleaseInterface(ref staticFlagGame);
                                    }
                                }
                                else
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to playback the {EventType.StaticFlagFullUpdate} because {nameof(IStaticFlagGame)} was not found.");
                                }
                            }
                            break;

                        case EventType.StaticFlagClaimed:
                            {
                                ref StaticFlagClaimed claimed = ref MemoryMarshal.AsRef<StaticFlagClaimed>(buffer);
                                if (!ad.PlayerIdMap.TryGetValue(claimed.PlayerId, out player))
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{head.Type} event for non-existent PlayerId {claimed.PlayerId}.");
                                    return;
                                }

                                IStaticFlagGame staticFlagGame = arena.GetInterface<IStaticFlagGame>();
                                if (staticFlagGame != null)
                                {
                                    try
                                    {
                                        staticFlagGame.FakeTouchFlag(player, claimed.FlagId);
                                    }
                                    finally
                                    {
                                        arena.ReleaseInterface(ref staticFlagGame);
                                    }
                                }
                                else
                                {
                                    _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"Unable to playback the {EventType.StaticFlagFullUpdate} because {nameof(IStaticFlagGame)} was not found.");
                                }
                            }
                            break;

                        case EventType.CarryFlagGameReset:
                            ref CarryFlagGameReset carryFlagGameReset = ref MemoryMarshal.AsRef<CarryFlagGameReset>(buffer);
                            S2C_FlagReset flagResetPacket = new(carryFlagGameReset.Freq, carryFlagGameReset.Points);
                            _network.SendToArena(arena, null, ref flagResetPacket, NetSendFlags.Reliable);
                            break;

                        case EventType.CarryFlagOnMap:
                            ref CarryFlagOnMap carryFlagOnMap = ref MemoryMarshal.AsRef<CarryFlagOnMap>(buffer);
                            S2C_FlagLocation flagLocationPacket = new(carryFlagOnMap.FlagId, carryFlagOnMap.X, carryFlagOnMap.Y, carryFlagOnMap.Freq);
                            _network.SendToArena(arena, null, ref flagLocationPacket, NetSendFlags.Reliable);
                            break;

                        case EventType.CarryFlagPickup:
                            ref CarryFlagPickup carryFlagPickup = ref MemoryMarshal.AsRef<CarryFlagPickup>(buffer);
                            if (ad.PlayerIdMap.TryGetValue(carryFlagPickup.PlayerId, out player))
                            {
                                S2C_FlagPickup flagPickupPacket = new(carryFlagPickup.FlagId, (short)player.Id);
                                _network.SendToArena(arena, null, ref flagPickupPacket, NetSendFlags.Reliable);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{head.Type} event for non-existent PlayerId {carryFlagPickup.PlayerId}.");
                            }
                            break;

                        case EventType.CarryFlagDrop:
                            ref CarryFlagDrop carryFlagDrop = ref MemoryMarshal.AsRef<CarryFlagDrop>(buffer);
                            if (ad.PlayerIdMap.TryGetValue(carryFlagDrop.PlayerId, out player))
                            {
                                S2C_FlagDrop flagDropPacket = new((short)player.Id);
                                _network.SendToArena(arena, null, ref flagDropPacket, NetSendFlags.Reliable);
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Warn, nameof(ReplayModule), arena, $"{head.Type} event for non-existent PlayerId {carryFlagDrop.PlayerId}.");
                            }
                            break;

                        case EventType.SecuritySeedChange:
                            ref SecuritySeedChange securitySeedChange = ref MemoryMarshal.AsRef<SecuritySeedChange>(buffer);
                            _securitySeedSync.OverrideArenaSeedInfo(arena, securitySeedChange.GreenSeed, securitySeedChange.DoorSeed, ServerTick.Now - securitySeedChange.TimeDelta);
                            break;

                        default:
                            break;
                    }
                }
                finally
                {
                    _recordBufferPool.Return(playbackBuffer.Buffer, true);
                }
            }

            static int ReadFromStream(Stream fileStream, Span<byte> remaining)
            {
                int totalRead = 0;
                int bytesRead;
                while (remaining.Length > 0 && (bytesRead = fileStream.Read(remaining)) > 0)
                {
                    totalRead += bytesRead;
                    remaining = remaining[bytesRead..];
                }

                return totalRead;
            }
        }

        private void MainloopWorkitem_EndPlayback(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                ad.State = ReplayState.None;
                ad.FileName = null;
                ad.StartedBy = null;
                ad.PlaybackTask = null;
            }

            _balls.TrySetBallCount(arena, null);

            ICarryFlagGame carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (carryFlagGame != null)
            {
                try
                {
                    // Reset the carry flag game and allow it to automatically restart.
                    carryFlagGame.ResetGame(arena, -1, 0, true);
                }
                finally
                {
                    arena.ReleaseInterface(ref carryFlagGame);
                }
            }
        }

        private void LogAndNotify(Arena arena, NotifyOption notifyOption, string message, Exception ex = null)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append(message);

                if (ex != null)
                {
                    do
                    {
                        sb.Append(' ');
                        sb.Append(ex.Message);
                    }
                    while ((ex = ex.InnerException) != null);
                }

                _logManager.LogA(LogLevel.Info, nameof(ReplayModule), arena, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            Notify(arena, notifyOption, message);
        }

        private void Notify(Arena arena, NotifyOption notifyOption, string message)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (notifyOption == NotifyOption.Player)
            {
                if (string.IsNullOrWhiteSpace(ad.StartedBy))
                    return;

                Player player = _playerData.FindPlayer(ad.StartedBy);
                if (player == null
                    || player.Arena != arena
                    || player.Status != PlayerState.Playing)
                {
                    return;
                }

                _chat.SendMessage(player, $"Replay: {message}");
            }
            else if (notifyOption == NotifyOption.Arena)
            {
                _chat.SendArenaMessage(arena, $"Replay: {message}");
            }
        }

        private void Notify(Arena arena, NotifyOption notifyOption, StringBuilder message)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (notifyOption == NotifyOption.Player)
            {
                if (string.IsNullOrWhiteSpace(ad.StartedBy))
                    return;

                Player player = _playerData.FindPlayer(ad.StartedBy);
                if (player == null
                    || player.Arena != arena
                    || player.Status != PlayerState.Playing)
                {
                    return;
                }

                _chat.SendMessage(player, $"Replay: {message}");
            }
            else if (notifyOption == NotifyOption.Arena)
            {
                _chat.SendArenaMessage(arena, $"Replay: {message}");
            }
        }

        private void GetWatching(Arena arena, HashSet<Player> set, short? freq = null)
        {
            if (arena == null || set == null)
                return;

            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Status == PlayerState.Playing
                        && player.Arena == arena
                        && player.Type != ClientType.Fake
                        && (freq == null || player.Freq == freq.Value))
                    {
                        set.Add(player);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void LockAllSpec(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.IFreqManagerEnforcerAdvisorRegistrationToken != null)
                return;

            ad.IFreqManagerEnforcerAdvisorRegistrationToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetWatching(arena, set);

                foreach (Player player in set)
                {
                    _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void UnlockAllSpec(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.IFreqManagerEnforcerAdvisorRegistrationToken != null)
            {
                arena.UnregisterAdvisor(ref ad.IFreqManagerEnforcerAdvisorRegistrationToken);
            }
        }

        #region Helper types

        private enum NotifyOption
        {
            /// <summary>
            /// No notification.
            /// </summary>
            None,

            /// <summary>
            /// The player that started the playback or recording is notified.
            /// </summary>
            Player,

            /// <summary>
            /// The arena is notified.
            /// </summary>
            Arena,

            // TODO: maybe add the ability to notify those that can use the ?replay command?
            //Staff,
        }

        private enum ReplayState
        {
            None,
            Recording,
            Playing,
        }

        private enum PlaybackCommand
        {
            Stop,
            Pause,
            Resume,
        }

        private readonly struct RecordBuffer
        {
            public readonly byte[] Buffer;
            public readonly int Length;

            public RecordBuffer(byte[] buffer, int length)
            {
                Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
                Length = length;
            }
        }

        private readonly struct PlaybackBuffer
        {
            public readonly Arena Arena;
            public readonly byte[] Buffer;
            public readonly int Length;

            public PlaybackBuffer(Arena arena, byte[] buffer, int length)
            {
                Arena = arena ?? throw new ArgumentNullException(nameof(arena));
                Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
                Length = length;
            }
        }

        private struct Settings
        {
            public readonly NotifyOption NotifyPlayback;
            public readonly NotifyOption NotifyPlaybackError;
            public readonly NotifyOption NotifyRecording;
            public readonly NotifyOption NotifyRecordingError;

            public readonly bool PlaybackMapCheckEnabled;
            public readonly bool PlaybackSpecFreqCheckEnabled;
            public readonly bool PlaybackLockTeams;

            public readonly bool RecordPublicChat;
            public readonly bool RecordPublicMacroChat;
            public readonly bool RecordSpecChat;
            public readonly bool RecordTeamChat;
            public readonly bool RecordArenaChat;

            public readonly bool PlaybackPublicChat;
            public readonly bool PlaybackPublicMacroChat;
            public readonly bool PlaybackSpecChat;
            public readonly bool PlaybackTeamChat;
            public readonly bool PlaybackArenaChat;

            private const string NotifyPlaybackHelpOptions = "None = no notifications, Player = the player that started the playback, Arena = players in the arena.";
            private const string NotifyRecordingHelpOptions = "None = no notifications, Player = the player that started the recording, Arena = players in the arena.";

            [ConfigHelp("Replay", "NotifyPlayback", ConfigScope.Arena, typeof(NotifyOption), DefaultValue = "Arena",
                Description = $"Who gets notifications about playback (start, stop, pause, and resume). {NotifyPlaybackHelpOptions}")]
            [ConfigHelp("Replay", "NotifyPlaybackError", ConfigScope.Arena, typeof(NotifyOption), DefaultValue = "Player",
                Description = $"Who gets notifications about playback errors. {NotifyPlaybackHelpOptions}")]
            [ConfigHelp("Replay", "NotifyRecording", ConfigScope.Arena, typeof(NotifyOption), DefaultValue = "Player",
                Description = $"Who gets notifications about recording (start and stop). {NotifyRecordingHelpOptions}")]
            [ConfigHelp("Replay", "NotifyRecordingError", ConfigScope.Arena, typeof(NotifyOption), DefaultValue = "Player",
                Description = $"Who gets notifications about recording errors. {NotifyRecordingHelpOptions}")]
            [ConfigHelp("Replay", "PlaybackMapCheckEnabled", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether to check if the map in the current arena matches the recording's map when starting a playback.")]
            [ConfigHelp("Replay", "PlaybackSpecFreqCheckEnabled", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether to check if the arena's spec freq matches the recording's.")]
            [ConfigHelp("Replay", "PlaybackLockTeams", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = $"Whether teams are locked during a playback.")]
            [ConfigHelp("Replay", "RecordPublicChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether public chat messages are recorded.")]
            [ConfigHelp("Replay", "RecordPublicMacroChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether public macro chat messages are recorded.")]
            [ConfigHelp("Replay", "RecordSpecChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether spectator chat messages are recorded.")]
            [ConfigHelp("Replay", "RecordTeamChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether team chat messages are recorded.")]
            [ConfigHelp("Replay", "RecordArenaChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether arena (green) chat messages are recorded.")]
            [ConfigHelp("Replay", "PlaybackPublicChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether public chat messages are played back.")]
            [ConfigHelp("Replay", "RecordPublicMacroChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether public macro chat messages are played back.")]
            [ConfigHelp("Replay", "PlaybackSpecChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether spectator chat messages are played back.")]
            [ConfigHelp("Replay", "PlaybackTeamChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether team chat messages are played back.")]
            [ConfigHelp("Replay", "PlaybackArenaChat", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
                Description = $"Whether arena (green) chat messages are played back.")]
            public Settings(IConfigManager configManager, ConfigHandle ch) : this()
            {
                if (configManager == null)
                    throw new ArgumentNullException(nameof(configManager));

                if (ch == null)
                    throw new ArgumentNullException(nameof(ch));

                // notification settings
                NotifyPlayback = configManager.GetEnum(ch, "Replay", "NotifyPlayback", NotifyOption.Arena);
                NotifyPlaybackError = configManager.GetEnum(ch, "Replay", "NotifyPlaybackError", NotifyOption.Player);
                NotifyRecording = configManager.GetEnum(ch, "Replay", "NotifyRecording", NotifyOption.Player);
                NotifyRecordingError = configManager.GetEnum(ch, "Replay", "NotifyRecordingError", NotifyOption.Player);

                // playback settings
                PlaybackMapCheckEnabled = configManager.GetInt(ch, "Replay", "PlaybackMapCheckEnabled", 1) != 0;
                PlaybackSpecFreqCheckEnabled = configManager.GetInt(ch, "Replay", "PlaybackSpecFreqCheckEnabled", 1) != 0;
                PlaybackLockTeams = configManager.GetInt(ch, "Replay", "PlaybackLockTeams", 0) != 0;

                // chat settings (recording)
                RecordPublicChat = configManager.GetInt(ch, "Replay", "RecordPublicChat", 1) != 0;
                RecordPublicMacroChat = configManager.GetInt(ch, "Replay", "RecordPublicMacroChat", 1) != 0;
                RecordSpecChat = configManager.GetInt(ch, "Replay", "RecordSpecChat", 1) != 0;
                RecordTeamChat = configManager.GetInt(ch, "Replay", "RecordTeamChat", 1) != 0;
                RecordArenaChat = configManager.GetInt(ch, "Replay", "RecordArenaChat", 1) != 0;

                // chat settings (playback)
                PlaybackPublicChat = configManager.GetInt(ch, "Replay", "PlaybackPublicChat", 1) != 0;
                PlaybackPublicMacroChat = configManager.GetInt(ch, "Replay", "PlaybackPublicMacroChat", 1) != 0;
                PlaybackSpecChat = configManager.GetInt(ch, "Replay", "PlaybackSpecChat", 1) != 0;
                PlaybackTeamChat = configManager.GetInt(ch, "Replay", "PlaybackTeamChat", 1) != 0;
                PlaybackArenaChat = configManager.GetInt(ch, "Replay", "PlaybackArenaChat", 1) != 0;
            }
        }

        private sealed class ArenaData : IDisposable
        {
            public Settings Settings;

            /// <summary>
            /// Advisor for locking players to spectator mode during a playback.
            /// </summary>
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor> IFreqManagerEnforcerAdvisorRegistrationToken;

            /// <summary>
            /// The current state.
            /// </summary>
            public ReplayState State;

            /// <summary>
            /// The name of the file being recorded or played.
            /// </summary>
            /// <remarks>
            /// This will be <see langword="null"/> until the the file is actually opened by the worker thread.
            /// </remarks>
            public string FileName;

            /// <summary>
            /// The name of the player that started the recording or playback.
            /// </summary>
            public string StartedBy;

            /// <summary>
            /// The current position of a playback (for showing the percentage).
            /// </summary>
            public double PlaybackPosition;

            /// <summary>
            /// Whether the playback is currently paused.
            /// </summary>
            public bool IsPlaybackPaused;

            /// <summary>
            /// The task for recording.
            /// </summary>
            public Task RecorderTask;

            /// <summary>
            /// The task for playback.
            /// </summary>
            public Task PlaybackTask;

            /// <summary>
            /// For playback, maps playerIds in a replay to the fake players.
            /// </summary>
            public readonly Dictionary<short, Player> PlayerIdMap = new();

            /// <summary>
            /// Queue for commands to the thread performing a playback.
            /// </summary>
            public readonly BlockingCollection<PlaybackCommand> PlaybackQueue = new();

            /// <summary>
            /// Queue of events to record.
            /// </summary>
            /// <remarks>
            /// Thread synchronization might seem strange for this field. However, here is the gist.
            /// The mainloop thread: 
            /// - is responsible for constructing the queue, 
            /// - is the sole producer (all events written to the queue are on the mainloop thread, this includes callbacks and packet handlers), 
            /// - is the one that will mark the queue complete for adding
            /// - and is the one that will dispose of the queue and the reference to it.
            /// The <see cref="RecorderTask"/> is the sole consumer.
            /// </remarks>
            public BlockingCollection<RecordBuffer> RecorderQueue;

            /// <summary>
            /// For thread synchronization.
            /// </summary>
            public readonly object Lock = new();

            #region IDisposable

            private bool isDisposed;

            private void Dispose(bool disposing)
            {
                if (!isDisposed)
                {
                    if (disposing)
                    {
                        PlaybackQueue?.Dispose();
                        RecorderQueue?.Dispose();
                    }

                    isDisposed = true;
                }
            }

            void IDisposable.Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }

            #endregion
        }

        #endregion
    }
}
