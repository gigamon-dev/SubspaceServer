using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map.Lvz;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for controlling LVZ objects.
    /// </summary>
    public class LvzObjects : IModule, ILvzObjects
    {
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private IMainloop _mainloop;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken<ILvzObjects> _interfaceRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private NonTransientObjectPool<LvzData> _lvzDataObjectPool = new(new LvzDataPooledObjectPolicy());

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMapData mapData,
            IMainloop mainloop,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _network.AddPacket(C2SPacketType.Rebroadcast, Packet_Rebroadcast);

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _commandManager.AddCommand("objon", Command_objon);
            _commandManager.AddCommand("objoff", Command_objoff);
            _commandManager.AddCommand("objset", Command_objset);
            _commandManager.AddCommand("objinfo", Command_objinfo);
            _commandManager.AddCommand("objlist", Command_objlist);

            _interfaceRegistrationToken = broker.RegisterInterface<ILvzObjects>(this);

            

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _interfaceRegistrationToken);

            _commandManager.RemoveCommand("objon", Command_objon);
            _commandManager.RemoveCommand("objoff", Command_objoff);
            _commandManager.RemoveCommand("objset", Command_objset);
            _commandManager.RemoveCommand("objinfo", Command_objinfo);
            _commandManager.RemoveCommand("objlist", Command_objlist);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _network.RemovePacket(C2SPacketType.Rebroadcast, Packet_Rebroadcast);

            _playerData.FreePlayerData(_pdKey);
            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region ILvzObjects

        void ILvzObjects.SendState(Player player)
        {
            SendState(player);
        }

        void ILvzObjects.Toggle(ITarget target, short id, bool isEnabled)
        {
            Toggle(target, id, isEnabled);
        }

        void ILvzObjects.ToggleSet(ITarget target, ReadOnlySpan<LvzToggle> set)
        {
            ToggleSet(target, set);
        }

        void ILvzObjects.Move(ITarget target, int id, int x, int y, int rx, int ry)
        {
            
        }

        void ILvzObjects.Image(ITarget target, int id, int image)
        {
            
        }

        void ILvzObjects.Layer(ITarget target, int id, int layer)
        {
            
        }

        void ILvzObjects.Timer(ITarget target, int id, int time)
        {
            
        }

        void ILvzObjects.Mode(ITarget target, int id, int mode)
        {
            
        }

        void ILvzObjects.Reset(Arena arena, int id)
        {
            
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<object id>",
            Description = "Toggles the specified object on.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objon(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                Toggle(target, id, true);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<object id>",
            Description = "Toggles the specified object off.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objoff(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                Toggle(target, id, false);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "[+|-]<object id 0> [+|-]<object id 1> ... [+|-]<object id N>",
            Description = "Toggles the specified objects on/off.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            int count = 0;
            foreach (char c in parameters)
                if (c == '+' || c == '-')
                    count++;

            Span<LvzToggle> set = stackalloc LvzToggle[count]; // Note: count is limited to max the chat message length

            int i = 0;
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while (i < set.Length 
                && (token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                bool enabled;

                if (token[0] == '+')
                    enabled = true;
                else if (token[0] == '-')
                    enabled = false;
                else
                    continue;

                if (!short.TryParse(token[1..], out short id) || id  < 0)
                    continue;

                set[i++] = new LvzToggle(id, enabled);
            }

            if (i > 0)
            {
                ToggleSet(target, set[..i]);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<id>",
            Description = "Reports all known information about the object.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objinfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            if (p.Arena == null || !p.Arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (!short.TryParse(parameters, out short objectId))
                return;

            int count = 0;

            lock (ad.Lock)
            {
                foreach (var lvzData in ad.List)
                {
                    if (lvzData.Default.Id == objectId)
                    {
                        if (lvzData.Current.IsMapObject)
                        {
                            _chat.SendMessage(p, $"lvz: Id:{objectId} Off:{lvzData.Off} Image:{lvzData.Current.ImageId} Layer:{lvzData.Current.Layer} Mode:{lvzData.Current.Mode} Time:{lvzData.Current.Time} map object coords ({lvzData.Current.MapX}, {lvzData.Current.MapY})");
                        }
                        else
                        {
                            _chat.SendMessage(p, $"lvz: Id:{objectId} Off:{lvzData.Off} Image:{lvzData.Current.ImageId} Layer:{lvzData.Current.Layer} Mode:{lvzData.Current.Mode} Time:{lvzData.Current.Time} screen object coords ({lvzData.Current.ScreenX}, {lvzData.Current.ScreenY}). X-offset: {lvzData.Current.ScreenXType}. Y-offset: {lvzData.Current.ScreenYType}.");
                        }

                        count++;
                    }
                }
            }

            if (count == 0)
            {
                _chat.SendMessage(p, $"Object {objectId} does not exist in any of the loaded LVZ files.");
                return;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "List all ServerControlled object id's. Use ?objinfo <id> for attributes\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objlist(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            if (p.Arena == null || !p.Arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            int count = 0;
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                lock (ad.Lock)
                {
                    foreach (var lvzData in ad.List)
                    {
                        if (lvzData.Current.Mode == DisplayMode.ServerControlled)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(lvzData.Current.Id);
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    _chat.SendMessage(p, $"{count} ServerControlled object{(count == 1 ? "" : "s")}:");
                    _chat.SendWrappedText(p, sb);
                }
                else
                {
                    _chat.SendMessage(p, $"0 ServerControlled objects.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private void Packet_Rebroadcast(Player p, byte[] data, int length)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (pd.Permission == BroadcastAuthorization.None)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), p, $"Attempted to broadcast without permission.");
                return;
            }

            if (length < 4)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), p, $"Invalid broadcast packet length ({length}).");
                return;
            }

            Span<byte> packet = data.AsSpan(0, length);
            int toPlayerId = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(1, 2));
            S2CPacketType type = (S2CPacketType)packet[3];

            if (type == S2CPacketType.Zero)
            {
                return;
            }
            else if (type == S2CPacketType.ToggleObj)
            {
                if (length < 6 || (length - 4) % ToggledObject.Length != 0)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), p, $"Invalid length for broadcasting a Toggle Object packet ({length}).");
                    return;
                }

                if (toPlayerId == -1)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                        return;

                    Span<ToggledObject> toggleSpan = MemoryMarshal.Cast<byte, ToggledObject>(packet[4..]);

                    lock (ad.Lock)
                    {
                        foreach (ref readonly ToggledObject toggleObj in toggleSpan)
                        {
                            UpdateArenaToggleTracking(arena, ad, toggleObj.Id, !toggleObj.IsDisabled);
                        }
                    }
                }
            }
            else if (type == S2CPacketType.MoveObj)
            {
                // TODO:
            }
            else
            {
                if (pd.Permission != BroadcastAuthorization.Any)
                {
                    _logManager.LogP(LogLevel.Info, nameof(LvzObjects), p, $"Not authorized to broadcast packet of type {type}.");
                    return;
                }
            }

            if (toPlayerId == -1)
            {
                _network.SendToArena(arena, null, packet[4..], NetSendFlags.Reliable);
            }
            else
            {
                Player toPlayer = _playerData.PidToPlayer(toPlayerId);
                if (toPlayer != null && toPlayer.Status == PlayerState.Playing && p.Arena == toPlayer.Arena)
                {
                    _network.SendToOne(toPlayer, packet[4..], NetSendFlags.Reliable);
                }
            }
        }

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create)
            {
                _arenaManager.HoldArena(arena);
                if (!_mainloop.QueueThreadPoolWorkItem(ThreadPoolWork_ArenaActionWork, arena))
                {
                    _logManager.LogA(LogLevel.Error, nameof(LvzObjects), arena, $"Error queueing up arena action work.");
                    _arenaManager.UnholdArena(arena);
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                foreach (LvzData lvzData in ad.List)
                {
                    _lvzDataObjectPool.Return(lvzData);
                }

                ad.List.Clear();
            }
        }

        private void ThreadPoolWork_ArenaActionWork(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            foreach (LvzFileInfo fileInfo in _mapData.LvzFilenames(arena))
            {
                try
                {
                    LvzReader.ReadObjects(fileInfo.Filename, ObjectDataRead);
                }
                catch (Exception ex)
                {
                    _logManager.LogA(LogLevel.Error, nameof(LvzObjects), arena, $"Error reading objects from lvz file '{fileInfo.Filename}'. {ex.Message}");
                }
            }

            _arenaManager.UnholdArena(arena);

            void ObjectDataRead(ReadOnlySpan<ObjectData> objectDataSpan)
            {
                lock (ad.Lock)
                {
                    foreach (ref readonly ObjectData objectData in objectDataSpan)
                    {
                        LvzData lvzData = _lvzDataObjectPool.Get();
                        lvzData.Off = true;
                        lvzData.Current = lvzData.Default = objectData;

                        ad.List.Add(lvzData);
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                    return;

                if (_capabilityManager.HasCapability(p, Constants.Capabilities.BroadcastAny))
                    pd.Permission = BroadcastAuthorization.Any;
                else if (_capabilityManager.HasCapability(p, Constants.Capabilities.BroadcastBot))
                    pd.Permission = BroadcastAuthorization.Bot;
            }
            else if(action == PlayerAction.EnterGame)
            {
                SendState(p);
            }
        }

        private void SendState(Player player)
        {
            if (player == null)
                return;

            if (player.Arena == null)
                return;

            if (!player.Arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            byte[] toggleBuffer = null;
            byte[] extraBuffer = null;

            try
            {
                Monitor.Enter(ad.Lock);
                
                int numToggleBytes = 1 + 2 * ad.ToggleDifferences;
                toggleBuffer = numToggleBytes > Constants.MaxPacket ? ArrayPool<byte>.Shared.Rent(numToggleBytes) : null;
                Span<byte> toggleSpan = toggleBuffer != null ? toggleBuffer : stackalloc byte[numToggleBytes];
                toggleSpan[0] = (byte)S2CPacketType.ToggleObj;
                int toggleCount = 0;

                int numExtraBytes = 1 + 11 * ad.ExtraDifferences;
                extraBuffer = numExtraBytes > Constants.MaxPacket ? ArrayPool<byte>.Shared.Rent(numExtraBytes) : null;
                Span<byte> extraSpan = extraBuffer != null ? extraBuffer : stackalloc byte[numExtraBytes];
                extraSpan[0] = (byte)S2CPacketType.MoveObj;
                int extraCount = 0;

                foreach (LvzData lvzData in ad.List)
                {
                    // toggle
                    if (!lvzData.Off)
                    {
                        if (toggleCount >= ad.ToggleDifferences)
                        {
                            _logManager.LogP(LogLevel.Error, nameof(LvzObjects), player, $"SendState: invalid arena state (tog_diffs), not enough memory has been allocated.");
                            break;
                        }

                        BinaryPrimitives.WriteInt16LittleEndian(
                            toggleSpan.Slice(1 + 2 * toggleCount, 2),
                            (short)(lvzData.Default.Id & 0x7FF)); // disabled bit is 0

                        toggleCount++;
                    }

                    // TODO: extra
                }

                Monitor.Exit(ad.Lock);

                // TODO: Access to ArenaData is synchronized. However, it probably isn't ok if another thread were to send an object packet to the same player between the unlock and these sends.
                // Like most synchronization in the server, that shouldn't happen long as everything is done by the mainloop thread (in which case none of the locking is needed).

                if (toggleCount > 0)
                    _network.SendToOne(player, toggleSpan[..(1 + 2 * toggleCount)], NetSendFlags.Reliable);

                if (extraCount > 0)
                    _network.SendToOne(player, extraSpan[..(1 + 11 * extraCount)], NetSendFlags.Reliable);
            }
            finally
            {
                if (toggleBuffer != null)
                    ArrayPool<byte>.Shared.Return(toggleBuffer);

                if (extraBuffer != null)
                    ArrayPool<byte>.Shared.Return(extraBuffer);
            }
        }

        private void Toggle(ITarget target, short id, bool enabled)
        {
            if (target == null)
                return;

            ReadOnlySpan<LvzToggle> toggleSpan = stackalloc LvzToggle[1] { new LvzToggle(id, enabled) };
            ToggleSet(target, toggleSpan);
        }

        private void ToggleSet(ITarget target, ReadOnlySpan<LvzToggle> set)
        {
            if (target == null)
                return;

            if (set.Length == 0)
                return;

            // Maximum Continuum allows at once.
            if (set.Length > 1023) // TODO: loop instead filling up to the max on each iteration
                throw new ArgumentOutOfRangeException(nameof(set), "Length was > 1023.");

            int packetLength = 1 + 2 * set.Length;

            byte[] byteArray = null;
            if (packetLength > Constants.MaxPacket)
                byteArray = ArrayPool<byte>.Shared.Rent(packetLength);

            try
            {
                Span<byte> packet = byteArray != null ? byteArray : stackalloc byte[packetLength];
                packet[0] = (byte)S2CPacketType.ToggleObj;

                ArenaData ad = null;
                if (target.TryGetArenaTarget(out Arena arena))
                {
                    arena.TryGetExtraData(_adKey, out ad);
                }

                for (int i = 0; i < set.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        packet.Slice(1 + 2 * i, 2),
                        (short)(set[i].Id & 0x7FF | (set[i].IsEnabled ? 0x0000 : 0x8000)));

                    if (arena != null && ad != null)
                    {
                        UpdateArenaToggleTracking(arena, ad, set[i].Id, set[i].IsEnabled);
                    }
                }

                _network.SendToTarget(target, packet, NetSendFlags.Reliable);
            }
            finally
            {
                if (byteArray != null)
                    ArrayPool<byte>.Shared.Return(byteArray);
            }
        }

        private void UpdateArenaToggleTracking(Arena arena, ArenaData ad, short id, bool isEnabled)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (ad == null)
                throw new ArgumentNullException(nameof(ad));

            lock (ad.Lock)
            {
                LvzData lvzData = ad.GetObjectData(id);
                if (lvzData != null && lvzData.Current.Time == 0)
                {
                    if (isEnabled && lvzData.Off)
                        ad.ToggleDifferences++;
                    else if (!isEnabled && !lvzData.Off)
                        ad.ToggleDifferences--;

                    lvzData.Off = !isEnabled;
                    _logManager.LogA(LogLevel.Drivel, nameof(LvzObjects), arena, $"Toggled object {id}. Tracking {ad.ToggleDifferences} objects.");
                }
            }
        }

        #region Helper types

        private enum BroadcastAuthorization
        {
            /// <summary>
            /// Not allowed to broadcast.
            /// </summary>
            None,

            /// <summary>
            /// Allowed to broadcast S2C 0x35 (ToggleObj) and 0x36 (MoveObj).
            /// </summary>
            /// <remarks>
            /// Assigned by the <see cref="Constants.Capabilities.BroadcastBot"/> capability.
            /// </remarks>
            Bot,

            /// <summary>
            /// Allows a player to broadcast arbritrary data to players in the same arena.
            /// </summary>
            /// <remarks>
            /// This extends the C2S 0x0A (Broadcast) to have power beyond that of manipulating Lvz objects.
            /// A player with this can send data of their choosing to players in the the same arena.
            /// The data can by anything, it is not bounded by any means. Therefore it can be very dangerous 
            /// to give the <see cref="Constants.Capabilities.BroadcastAny"/> capability.
            /// </remarks>
            Any,
        }

        public class LvzData
        {
            public bool Off;

            public ObjectData Default;
            public ObjectData Current;
        }

        private class ArenaData
        {
            public readonly List<LvzData> List = new();

            public int ToggleDifferences = 0;
            public int ExtraDifferences = 0;

            public readonly object Lock = new();

            public LvzData GetObjectData(short objectId)
            {
                lock (Lock)
                {
                    foreach (var lvzObject in List)
                    {
                        if (lvzObject.Default.Id == objectId)
                            return lvzObject;
                    }

                    return null;
                }
            }
        }

        private class PlayerData
        {
            public BroadcastAuthorization Permission = BroadcastAuthorization.None;
        }

        public class LvzDataPooledObjectPolicy : PooledObjectPolicy<LvzData>
        {
            public override LvzData Create()
            {
                return new LvzData()
                {
                    Off = true,
                    Default = default,
                    Current = default,
                };
            }

            public override bool Return(LvzData obj)
            {
                if (obj == null)
                    return false;

                obj.Off = true;
                obj.Default = default;
                obj.Current = default;

                return true;
            }
        }

        #endregion
    }
}
