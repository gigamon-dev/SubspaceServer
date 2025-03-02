using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map.Lvz;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for controlling LVZ objects.
    /// </summary>
    [CoreModuleInfo]
    public sealed class LvzObjects(
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
        IPlayerData playerData) : IModule, ILvzObjects
    {
        private const string CommonHelpText = "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist";

        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMapData _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private InterfaceRegistrationToken<ILvzObjects>? _interfaceRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private static readonly DefaultObjectPool<LvzData> _lvzDataObjectPool = new(new DefaultPooledObjectPolicy<LvzData>(), Constants.TargetArenaCount * ushort.MaxValue);

        /// <summary>
        /// Continuum supports 0x35 (Toggle LVZ) packets up to a maximum of 2048 bytes.
        /// </summary>
        private const int MaxTogglePacketLength = 2048;

        /// <summary>
        /// Continuum supports 0x36 (Change LVZ) packets up to a maximum of 2048 bytes.
        /// </summary>
        private const int MaxChangePacketLength = 2048;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _network.AddPacket(C2SPacketType.Rebroadcast, Packet_Rebroadcast);

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _commandManager.AddCommand("objon", Command_objon);
            _commandManager.AddCommand("objoff", Command_objoff);
            _commandManager.AddCommand("objset", Command_objset);
            _commandManager.AddCommand("objmove", Command_objmove);
            _commandManager.AddCommand("objimage", Command_objimage);
            _commandManager.AddCommand("objlayer", Command_objlayer);
            _commandManager.AddCommand("objtimer", Command_objtimer);
            _commandManager.AddCommand("objmode", Command_objmode);
            _commandManager.AddCommand("objinfo", Command_objinfo);
            _commandManager.AddCommand("objlist", Command_objlist);

            _interfaceRegistrationToken = broker.RegisterInterface<ILvzObjects>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _interfaceRegistrationToken);

            _commandManager.RemoveCommand("objon", Command_objon);
            _commandManager.RemoveCommand("objoff", Command_objoff);
            _commandManager.RemoveCommand("objset", Command_objset);
            _commandManager.RemoveCommand("objmove", Command_objmove);
            _commandManager.RemoveCommand("objimage", Command_objimage);
            _commandManager.RemoveCommand("objlayer", Command_objlayer);
            _commandManager.RemoveCommand("objtimer", Command_objtimer);
            _commandManager.RemoveCommand("objmode", Command_objmode);
            _commandManager.RemoveCommand("objinfo", Command_objinfo);
            _commandManager.RemoveCommand("objlist", Command_objlist);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _network.RemovePacket(C2SPacketType.Rebroadcast, Packet_Rebroadcast);

            _playerData.FreePlayerData(ref _pdKey);
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region ILvzObjects

        void ILvzObjects.SendState(Player player)
        {
            if (player is null)
                return;

            if (player.Arena is null)
                return;

            if (!player.Arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            lock (arenaData.Lock)
            {
                // Sending changes first, so that if there is a change, the old state won't be shown (toggled on) before it's changed.
                SendArenaChanges(player, arenaData);
                SendArenaToggles(player, arenaData);
            }

            void SendArenaChanges(Player player, ArenaData arenaData)
            {
                Span<byte> changeBytes = stackalloc byte[MaxChangePacketLength];
                changeBytes[0] = (byte)S2CPacketType.ChangeLVZ;
                Span<LvzObjectChange> changeSpan = MemoryMarshal.Cast<byte, LvzObjectChange>(changeBytes[1..]);
                int index = 0;

                foreach (LvzData lvzData in arenaData.List)
                {
                    ObjectChange change = ObjectData.CalculateChange(ref lvzData.Default, ref lvzData.Current);
                    if (change.HasChange)
                    {
                        if (index >= changeSpan.Length)
                        {
                            _network.SendToOne(player, changeBytes[..(1 + index * LvzObjectChange.Length)], NetSendFlags.Reliable);
                            index = 0;
                        }

                        changeSpan[index++] = new LvzObjectChange(change, lvzData.Current);
                    }
                }

                if (index > 0)
                {
                    _network.SendToOne(player, changeBytes[..(1 + index * LvzObjectChange.Length)], NetSendFlags.Reliable);
                }
            }

            void SendArenaToggles(Player player, ArenaData arenaData)
            {
                Span<byte> toggleBytes = stackalloc byte[MaxTogglePacketLength];
                toggleBytes[0] = (byte)S2CPacketType.ToggleLVZ;
                Span<LvzObjectToggle> toggleSpan = MemoryMarshal.Cast<byte, LvzObjectToggle>(toggleBytes[1..]);
                int index = 0;

                foreach (LvzData lvzData in arenaData.List)
                {
                    if (!lvzData.Off)
                    {
                        if (index >= toggleSpan.Length)
                        {
                            _network.SendToOne(player, toggleBytes[..(1 + index * LvzObjectToggle.Length)], NetSendFlags.Reliable);
                            index = 0;
                        }

                        toggleSpan[index++] = new LvzObjectToggle(lvzData.Default.Id, true);
                    }
                }

                if (index > 0)
                {
                    _network.SendToOne(player, toggleBytes[..(1 + index * LvzObjectToggle.Length)], NetSendFlags.Reliable);
                }
            }
        }

        void ILvzObjects.Toggle(ITarget target, short id, bool isEnabled)
        {
            if (target is null)
                return;

            ReadOnlySpan<LvzObjectToggle> toggleSpan = [new LvzObjectToggle(id, isEnabled)];
            ((ILvzObjects)this).Toggle(target, toggleSpan);
        }

        void ILvzObjects.Toggle(ITarget target, ReadOnlySpan<LvzObjectToggle> set)
        {
            if (target is null)
                return;

            if (set.IsEmpty)
                return;

            ArenaData? arenaData = null;
            if (target.TryGetArenaTarget(out Arena? arena))
            {
                arena.TryGetExtraData(_adKey, out arenaData);
            }

            Span<byte> packetBytes = stackalloc byte[int.Clamp(1 + 2 * set.Length, 3, MaxTogglePacketLength)];
            packetBytes[0] = (byte)S2CPacketType.ToggleLVZ;
            Span<LvzObjectToggle> toggleSpan = MemoryMarshal.Cast<byte, LvzObjectToggle>(packetBytes[1..]);
            int index = 0;

            foreach (ref readonly LvzObjectToggle toggle in set)
            {
                if (index >= toggleSpan.Length)
                {
                    _network.SendToTarget(target, packetBytes[..(1 + index * LvzObjectToggle.Length)], NetSendFlags.Reliable);
                    index = 0;
                }

                toggleSpan[index++] = toggle;

                if (arena is not null && arenaData is not null)
                {
                    UpdateArenaToggleTracking(arena, arenaData, toggle.Id, toggle.IsEnabled);
                }
            }

            if (index > 0)
            {
                _network.SendToTarget(target, packetBytes[..(1 + index * LvzObjectToggle.Length)], NetSendFlags.Reliable);
            }
        }

        void ILvzObjects.SetPosition(ITarget target, short id, short x, short y, ScreenOffset offsetX, ScreenOffset offsetY)
        {
            ChangeObject(target, id, SetPosition, (x, y, offsetX, offsetY));

            static void SetPosition(ref LvzObjectChange objectChange, (short x, short y, ScreenOffset offsetX, ScreenOffset offsetY) position)
            {
                (short x, short y, ScreenOffset offsetX, ScreenOffset offsetY) = position;

                objectChange.Change.Position = true;

                if (objectChange.Data.IsMapObject)
                {
                    objectChange.Data.MapX = SanitizeMapCoord(x);
                    objectChange.Data.MapY = SanitizeMapCoord(y);
                }
                else
                {
                    objectChange.Data.ScreenX = x;
                    objectChange.Data.ScreenY = y;
                    objectChange.Data.ScreenXOffset = offsetX;
                    objectChange.Data.ScreenYOffset = offsetY;
                }
            }

            // Makes sure the position LVZ is within the map
            static short SanitizeMapCoord(short coord)
            {
                // 1024 * 16 - 1 = 16383
                return short.Clamp(coord, 0, 16383);
            }
        }

        void ILvzObjects.SetImage(ITarget target, short id, byte imageId)
        {
            ChangeObject(target, id, SetImage, imageId);

            static void SetImage(ref LvzObjectChange objectChange, byte imageId)
            {
                objectChange.Change.Image = true;
                objectChange.Data.ImageId = imageId;
            }
        }

        void ILvzObjects.SetLayer(ITarget target, short id, DisplayLayer layer)
        {
            ChangeObject(target, id, SetLayer, layer);

            static void SetLayer(ref LvzObjectChange objectChange, DisplayLayer layer)
            {
                objectChange.Change.Layer = true;
                objectChange.Data.Layer = layer;
            }
        }

        void ILvzObjects.SetTimer(ITarget target, short id, ushort time)
        {
            ChangeObject(target, id, SetTimer, time);

            static void SetTimer(ref LvzObjectChange objectChange, ushort time)
            {
                objectChange.Change.Time = true;
                objectChange.Data.Time = time;
            }
        }

        void ILvzObjects.SetMode(ITarget target, short id, DisplayMode mode)
        {
            ChangeObject(target, id, SetMode, mode);

            static void SetMode(ref LvzObjectChange objectChange, DisplayMode mode)
            {
                objectChange.Change.Mode = true;
                objectChange.Data.Mode = mode;
            }
        }

        void ILvzObjects.Set(ITarget target, ReadOnlySpan<LvzObjectChange> changes)
        {
            if (target is null)
                return;

            if (changes.IsEmpty)
                return;

            if (!target.TryGetArenaTarget(out Arena? arena))
            {
                if (target.TryGetPlayerTarget(out Player? player))
                    arena = player.Arena;
            }

            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            Span<byte> changeBytes = stackalloc byte[int.Clamp(1 + (changes.Length * LvzObjectChange.Length), 1 + (1 * LvzObjectChange.Length), MaxChangePacketLength)];
            changeBytes[0] = (byte)S2CPacketType.ChangeLVZ;
            Span<LvzObjectChange> changeSpan = MemoryMarshal.Cast<byte, LvzObjectChange>(changeBytes[1..]);

            int index = 0;
            foreach (ref readonly LvzObjectChange change in changes)
            {
                if (index >= changeSpan.Length)
                {
                    _network.SendToTarget(target, changeBytes[..(1 + (index * LvzObjectChange.Length))], NetSendFlags.Reliable);
                    index = 0;
                }

                changeSpan[index++] = change;
            }

            if (index >= 0)
            {
                _network.SendToTarget(target, changeBytes[..(1 + (index * LvzObjectChange.Length))], NetSendFlags.Reliable);
                index = 0;
            }

            if (target.Type == TargetType.Arena)
            {
                lock (arenaData.Lock)
                {
                    foreach (ref readonly LvzObjectChange change in changes)
                    {
                        ref readonly ObjectData changedObject = ref change.Data;
                        LvzData? lvzData = arenaData.GetObjectData(changedObject.Id);
                        if (lvzData is null)
                            return;

                        if (changedObject != lvzData.Default)
                        {
                            if (lvzData.Current == lvzData.Default)
                            {
                                // Currently using the default value and changing it to a non-default value.
                                arenaData.ExtraDifferences++;
                            }
                        }
                        else
                        {
                            if (lvzData.Current != lvzData.Default)
                            {
                                // Currently using a non-default value and changing it back to the default value.
                                arenaData.ExtraDifferences--;
                            }
                        }

                        lvzData.Current = changedObject;

                        _logManager.LogA(LogLevel.Drivel, nameof(LvzObjects), arena, $"Changed object {changedObject.Id}. Tracking {arenaData.ExtraDifferences} changed objects.");
                    }
                }
            }
        }

        void ILvzObjects.SetAndToggle(ITarget target, ReadOnlySpan<LvzObjectChange> changes, ReadOnlySpan<LvzObjectToggle> toggles)
        {
            ((ILvzObjects)this).Set(target, changes);
            ((ILvzObjects)this).Toggle(target, toggles);
        }

        void ILvzObjects.Reset(Arena arena, short id, bool sendChanges)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            lock (arenaData.Lock)
            {
                LvzData? lvzData = arenaData.GetObjectData(id);
                if (lvzData is null)
                    return;

                if (!lvzData.Off)
                {
                    ((ILvzObjects)this).Toggle(arena, id, false);
                }

                ObjectChange change = ObjectData.CalculateChange(ref lvzData.Default, ref lvzData.Current);
                if (change.HasChange)
                {
                    if (sendChanges)
                    {
                        Span<LvzObjectChange> changeSpan = [new LvzObjectChange(change, lvzData.Default)];
                        ((ILvzObjects)this).Set(arena, changeSpan);
                    }
                    else
                    {
                        lvzData.Current = lvzData.Default;
                        arenaData.ExtraDifferences--;
                    }
                }
            }
        }

        void ILvzObjects.Reset(Arena arena, bool sendChanges)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            lock (arenaData.Lock)
            {
                Span<byte> toggleBytes = stackalloc byte[MaxTogglePacketLength];
                toggleBytes[0] = (byte)S2CPacketType.ToggleLVZ;
                Span<LvzObjectToggle> toggleSpan = MemoryMarshal.Cast<byte, LvzObjectToggle>(toggleBytes[1..]);

                Span<byte> changeBytes = sendChanges ? stackalloc byte[MaxChangePacketLength] : stackalloc byte[1];
                changeBytes[0] = (byte)S2CPacketType.ChangeLVZ;
                Span<LvzObjectChange> changeSpan = MemoryMarshal.Cast<byte, LvzObjectChange>(changeBytes[1..]);

                int toggleIndex = 0;
                int changeIndex = 0;

                foreach (LvzData lvzData in arenaData.List)
                {
                    // Check if the object needs to be toggled off.
                    if (!lvzData.Off)
                    {
                        if (toggleIndex >= toggleSpan.Length)
                        {
                            _network.SendToArena(arena, null, toggleBytes[..(1 + toggleIndex * LvzObjectToggle.Length)], NetSendFlags.Reliable);
                            toggleIndex = 0;
                        }

                        lvzData.Off = true;
                        arenaData.ToggleDifferences--;

                        toggleSpan[toggleIndex++] = new LvzObjectToggle(lvzData.Default.Id, true);
                    }

                    // Check for object changes.
                    ObjectChange change = ObjectData.CalculateChange(ref lvzData.Default, ref lvzData.Current);
                    if (change.HasChange)
                    {
                        if (changeIndex >= changeSpan.Length)
                        {
                            _network.SendToArena(arena, null, changeBytes[..(1 + (changeIndex * LvzObjectChange.Length))], NetSendFlags.Reliable);
                            changeIndex = 0;
                        }

                        lvzData.Current = lvzData.Default;
                        arenaData.ExtraDifferences--;

                        if (sendChanges)
                            changeSpan[changeIndex++] = new LvzObjectChange(change, lvzData.Default);
                    }
                }

                if (toggleIndex >= 0)
                {
                    _network.SendToArena(arena, null, toggleBytes[..(1 + toggleIndex * LvzObjectToggle.Length)], NetSendFlags.Reliable);
                    toggleIndex = 0;
                }

                if (changeIndex >= 0)
                {
                    _network.SendToArena(arena, null, changeBytes[..(1 + (changeIndex * LvzObjectChange.Length))], NetSendFlags.Reliable);
                    changeIndex = 0;
                }
            }
        }

        bool ILvzObjects.TryGetDefaultInfo(Arena arena, short id, out bool isEnabled, out ObjectData objectData)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
            {
                isEnabled = default;
                objectData = default;
                return false;
            }

            lock (arenaData.Lock)
            {
                LvzData? lvzData = arenaData.GetObjectData(id);
                if (lvzData is not null)
                {
                    isEnabled = !lvzData.Off;
                    objectData = lvzData.Default;
                    return true;
                }
            }

            // Not found
            isEnabled = default;
            objectData = default;
            return false;
        }

        bool ILvzObjects.TryGetCurrentInfo(Arena arena, short id, out bool isEnabled, out ObjectData objectData)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
            {
                isEnabled = default;
                objectData = default;
                return false;
            }

            lock (arenaData.Lock)
            {
                LvzData? lvzData = arenaData.GetObjectData(id);
                if (lvzData is not null)
                {
                    isEnabled = !lvzData.Off;
                    objectData = lvzData.Current;
                    return true;
                }
            }

            // Not found
            isEnabled = default;
            objectData = default;
            return false;
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<object id>",
            Description = $"""
                Toggles the specified object on.
                {CommonHelpText}
                """)]
        private void Command_objon(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                ((ILvzObjects)this).Toggle(target, id, true);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<object id>",
            Description = $"""
                Toggles the specified object off.
                {CommonHelpText}
                """)]
        private void Command_objoff(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                ((ILvzObjects)this).Toggle(target, id, false);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "[+|-]<object id 0> [+|-]<object id 1> ... [+|-]<object id N>",
            Description = $"""
                Toggles the specified objects on/off.
                {CommonHelpText}
                """)]
        private void Command_objset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            int count = 0;
            foreach (char c in parameters)
                if (c == '+' || c == '-')
                    count++;

            Span<LvzObjectToggle> set = stackalloc LvzObjectToggle[count]; // Note: count is limited to max the chat message length

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

                if (!short.TryParse(token[1..], out short id) || id < 0)
                    continue;

                set[i++] = new LvzObjectToggle(id, !enabled);
            }

            if (i > 0)
            {
                ((ILvzObjects)this).Toggle(target, set[..i]);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<id> <x> <y> (for map obj) or <id> [CBSGFETROWV]<x> [CBSGFETROWV]<y> (screen obj)",
            Description = $"""
                Moves an LVZ map or screen object. Coordinates are in pixels.
                {CommonHelpText}
                """)]
        private void Command_objmove(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> idStr = remaining.GetToken(' ', out remaining);
            ReadOnlySpan<char> xStr = remaining.GetToken(' ', out remaining);
            ReadOnlySpan<char> yStr = remaining.GetToken(' ', out _);

            if (!idStr.IsEmpty
                && !xStr.IsEmpty
                && !yStr.IsEmpty
                && short.TryParse(idStr, out short id)
                && TryParseCoord(xStr, out short x, out ScreenOffset offsetX)
                && TryParseCoord(yStr, out short y, out ScreenOffset offsetY))
            {
                ((ILvzObjects)this).SetPosition(target, id, x, y, offsetX, offsetY);
            }
            else
            {
                _chat.SendMessage(player, "Invalid syntax. Please read help for ?objmove");
            }

            static bool TryParseCoord(ReadOnlySpan<char> coordStr, out short coord, out ScreenOffset offset)
            {
                if (!coordStr.IsEmpty)
                {
                    if (char.IsNumber(coordStr[0]))
                    {
                        // Normal map coordinate
                        offset = ScreenOffset.Normal;
                        return short.TryParse(coordStr, out coord);
                    }
                    else if (coordStr.Length > 1
                        && Enum.TryParse(coordStr[..1], true, out offset)
                        && short.TryParse(coordStr[1..], out coord))
                    {
                        // Relative screen coordinate
                        return true;
                    }
                }

                coord = 0;
                offset = ScreenOffset.Normal;
                return false;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<id> <image>",
            Description = $"""
                Change the image associated with an object id.
                {CommonHelpText}
                """)]
        private void Command_objimage(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> idStr = parameters.GetToken(' ', out ReadOnlySpan<char> imageStr);
            if (!idStr.IsEmpty
                && short.TryParse(idStr, out short id)
                && !(imageStr = imageStr.TrimStart(' ')).IsEmpty
                && byte.TryParse(imageStr, out byte image))
            {
                ((ILvzObjects)this).SetImage(target, id, image);
            }
            else
            {
                _chat.SendMessage(player, "Invalid syntax. Please read help for ?objimage");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<id> <layer code>",
            Description = $"""
                Change the layer associated with an object id. Layer codes:
                BelowAll  AfterBackground  AfterTiles  AfterWeapons
                AfterShips  AfterGauges  AfterChat  TopMost
                {CommonHelpText}
                """)]
        private void Command_objlayer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> idStr = parameters.GetToken(' ', out ReadOnlySpan<char> layerStr);

            if (!idStr.IsEmpty
                && short.TryParse(idStr, out short id)
                && !(layerStr = layerStr.TrimStart(' ')).IsEmpty
                && Enum.TryParse(layerStr, true, out DisplayLayer layer))
            {
                ((ILvzObjects)this).SetLayer(target, id, layer);
            }
            else
            {
                _chat.SendMessage(player, "Invalid syntax. Please read help for ?objlayer");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<id> <time>",
            Description = $"""
                Change the timer associated with an object id.
                {CommonHelpText}
                """)]
        private void Command_objtimer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> idStr = parameters.GetToken(' ', out ReadOnlySpan<char> timeStr);
            if (!idStr.IsEmpty
                && short.TryParse(idStr, out short id)
                && !(timeStr = timeStr.TrimStart(' ')).IsEmpty
                && ushort.TryParse(timeStr, out ushort time))
            {
                ((ILvzObjects)this).SetTimer(target, id, time);
            }
            else
            {
                _chat.SendMessage(player, "Invalid syntax. Please read help for ?objtimer");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<id> <mode code>",
            Description = $"""
                Change the mode associated with an object id. Mode codes:
                ShowAlways  EnterZone  EnterArena  Kill  Death  ServerControlled
                {CommonHelpText}
                """)]
        private void Command_objmode(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> idStr = parameters.GetToken(' ', out ReadOnlySpan<char> modeStr);
            if (!idStr.IsEmpty
                && short.TryParse(idStr, out short id)
                && !(modeStr = modeStr.TrimStart(' ')).IsEmpty
                && Enum.TryParse(modeStr, true, out DisplayMode mode))
            {
                ((ILvzObjects)this).SetMode(target, id, mode);
            }
            else
            {
                _chat.SendMessage(player, "Invalid syntax. Please read help for ?objmode");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<id>",
            Description = $"""
                Reports all known information about the object.
                {CommonHelpText}
                """)]
        private void Command_objinfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is null || !player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
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
                            _chat.SendMessage(player, $"lvz: Id:{objectId} Off:{lvzData.Off} Image:{lvzData.Current.ImageId} Layer:{lvzData.Current.Layer} Mode:{lvzData.Current.Mode} Time:{lvzData.Current.Time} map object coords ({lvzData.Current.MapX}, {lvzData.Current.MapY})");
                        }
                        else
                        {
                            _chat.SendMessage(player, $"lvz: Id:{objectId} Off:{lvzData.Off} Image:{lvzData.Current.ImageId} Layer:{lvzData.Current.Layer} Mode:{lvzData.Current.Mode} Time:{lvzData.Current.Time} screen object coords ({lvzData.Current.ScreenX}, {lvzData.Current.ScreenY}). X-offset: {lvzData.Current.ScreenXOffset}. Y-offset: {lvzData.Current.ScreenYOffset}.");
                        }

                        count++;
                    }
                }
            }

            if (count == 0)
            {
                _chat.SendMessage(player, $"Object {objectId} does not exist in any of the loaded LVZ files.");
                return;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = $"""
                List all ServerControlled object id's. Use ?objinfo <id> for attributes
                {CommonHelpText}
                """)]
        private void Command_objlist(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is null || !player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
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
                    _chat.SendMessage(player, $"{count} ServerControlled object{(count == 1 ? "" : "s")}:");
                    _chat.SendWrappedText(player, sb);
                }
                else
                {
                    _chat.SendMessage(player, $"0 ServerControlled objects.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private void Packet_Rebroadcast(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (pd.Permission == BroadcastAuthorization.None)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), player, "Attempted to broadcast without permission.");
                return;
            }

            if (data.Length < 4)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), player, $"Invalid broadcast packet (length={data.Length}).");
                return;
            }

            int toPlayerId = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(1, 2));
            S2CPacketType type = (S2CPacketType)data[3];

            if (type == S2CPacketType.Zero)
            {
                return;
            }
            else if (type == S2CPacketType.ToggleLVZ)
            {
                if (data.Length < (3 + 1 + LvzObjectToggle.Length) || (data.Length - 4) % LvzObjectToggle.Length != 0)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), player, $"Invalid length for broadcasting a Toggle Object packet ({data.Length}).");
                    return;
                }

                if (toPlayerId == -1) // To whole arena.
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                        return;

                    Span<LvzObjectToggle> toggleSpan = MemoryMarshal.Cast<byte, LvzObjectToggle>(data[4..]);

                    lock (arenaData.Lock)
                    {
                        foreach (ref readonly LvzObjectToggle toggleObj in toggleSpan)
                        {
                            UpdateArenaToggleTracking(arena, arenaData, toggleObj.Id, toggleObj.IsEnabled);
                        }
                    }
                }
            }
            else if (type == S2CPacketType.ChangeLVZ)
            {
                if (data.Length < (3 + 1 + LvzObjectChange.Length) || (data.Length - 4) % LvzObjectChange.Length != 0)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(LvzObjects), player, $"Invalid length for broadcasting a Toggle Object packet ({data.Length}).");
                    return;
                }

                if (toPlayerId == -1) // To whole arena.
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                        return;

                    Span<LvzObjectChange> changeSpan = MemoryMarshal.Cast<byte, LvzObjectChange>(data[4..]);

                    lock (arenaData.Lock)
                    {
                        foreach (ref LvzObjectChange change in changeSpan)
                        {
                            LvzData? lvzData = arenaData.GetObjectData(change.Data.Id);
                            if (lvzData is null)
                                continue;

                            bool isChanged = lvzData.Current != lvzData.Default;
                            bool isChanging = false;

                            // Note: MapX and MapY include the entire bitfield, so don't have to worry about whether it's a screen vs map object.
                            if (change.Change.Position
                                && (change.Data.MapX != lvzData.Default.MapX || change.Data.MapY != lvzData.Default.MapY))
                            {
                                isChanging = true;
                                lvzData.Current.MapX = change.Data.MapX;
                                lvzData.Current.MapY = change.Data.MapY;
                            }

                            if (change.Change.Image
                                && change.Data.ImageId != lvzData.Default.ImageId)
                            {
                                isChanging = true;
                                lvzData.Current.ImageId = change.Data.ImageId;
                            }

                            if (change.Change.Layer
                                && change.Data.Layer != lvzData.Default.Layer)
                            {
                                isChanging = true;
                                lvzData.Current.Layer = change.Data.Layer;
                            }

                            if (change.Change.Mode
                                && change.Data.Mode != lvzData.Default.Mode)
                            {
                                isChanging = true;
                                lvzData.Current.Mode = change.Data.Mode;
                            }

                            if (change.Change.Time
                                && change.Data.Time != lvzData.Default.Time)
                            {
                                isChanging = true;
                                lvzData.Current.Time = change.Data.Time;
                            }

                            if (isChanging && !isChanged)
                                arenaData.ExtraDifferences++;
                            else if (!isChanging && isChanged)
                                arenaData.ExtraDifferences--;

                            _logManager.LogA(LogLevel.Drivel, nameof(LvzObjects), arena, $"Changed object {change.Data.Id}. Tracking {arenaData.ExtraDifferences} changed objects.");
                        }
                    }
                }
            }
            else
            {
                if (pd.Permission != BroadcastAuthorization.Any)
                {
                    _logManager.LogP(LogLevel.Info, nameof(LvzObjects), player, $"Not authorized to broadcast packet of type {type}.");
                    return;
                }
            }

            if (toPlayerId == -1)
            {
                _network.SendToArena(arena, null, data[4..], NetSendFlags.Reliable);
            }
            else
            {
                Player? toPlayer = _playerData.PidToPlayer(toPlayerId);
                if (toPlayer is not null && toPlayer.Status == PlayerState.Playing && player.Arena == toPlayer.Arena)
                {
                    _network.SendToOne(toPlayer, data[4..], NetSendFlags.Reliable);
                }
            }
        }

        #region Callbacks

        private async void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.PreCreate)
            {
                // NOTE: LVZ files are loaded on ArenaAction.PreCreate so that LVZ functionality will be ready to use on ArenaAction.Create.

                _arenaManager.AddHold(arena);

                await InitializeArenaAsync(arena).ConfigureAwait(false);

                _arenaManager.RemoveHold(arena);
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

        private async Task InitializeArenaAsync(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            await foreach (LvzFileInfo fileInfo in _mapData.LvzFilenamesAsync(arena).ConfigureAwait(false))
            {
                try
                {
                    LvzReader.ReadObjects(fileInfo.Filename, ObjectDataRead, ad);
                }
                catch (Exception ex)
                {
                    _logManager.LogA(LogLevel.Error, nameof(LvzObjects), arena, $"Error reading objects from lvz file '{fileInfo.Filename}'. {ex.Message}");
                }
            }
        }

        private static void ObjectDataRead(ReadOnlySpan<ObjectData> objectDataSpan, ArenaData ad)
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

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                if (_capabilityManager.HasCapability(player, Constants.Capabilities.BroadcastAny))
                    pd.Permission = BroadcastAuthorization.Any;
                else if (_capabilityManager.HasCapability(player, Constants.Capabilities.BroadcastBot))
                    pd.Permission = BroadcastAuthorization.Bot;
            }
            else if (action == PlayerAction.EnterGame)
            {
                ((ILvzObjects)this).SendState(player);
            }
        }

        #endregion

        private void UpdateArenaToggleTracking(Arena arena, ArenaData ad, short id, bool isEnabled)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(ad);

            lock (ad.Lock)
            {
                LvzData? lvzData = ad.GetObjectData(id);
                if (lvzData is not null && lvzData.Current.Time == 0)
                {
                    if (isEnabled && lvzData.Off)
                        ad.ToggleDifferences++;
                    else if (!isEnabled && !lvzData.Off)
                        ad.ToggleDifferences--;

                    lvzData.Off = !isEnabled;
                    _logManager.LogA(LogLevel.Drivel, nameof(LvzObjects), arena, $"Toggled object {id}. Tracking {ad.ToggleDifferences} toggled objects.");
                }
            }
        }

        private void ChangeObject<TState>(ITarget target, short id, ChangeObjectDelegate<TState> changeCallback, TState state)
        {
            if (!target.TryGetArenaTarget(out Arena? arena))
            {
                if (target.TryGetPlayerTarget(out Player? player))
                    arena = player.Arena;
            }

            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                return;

            Span<byte> packetBytes = stackalloc byte[1 + LvzObjectChange.Length];
            packetBytes[0] = (byte)S2CPacketType.ChangeLVZ;
            ref LvzObjectChange objectChange = ref MemoryMarshal.AsRef<LvzObjectChange>(packetBytes[1..]);

            lock (arenaData.Lock)
            {
                LvzData? lvzData = arenaData.GetObjectData(id);
                if (lvzData is null)
                    return;

                objectChange.Data = lvzData.Current;

                changeCallback(ref objectChange, state);

                if (target.Type == TargetType.Arena)
                {
                    if (objectChange.Data != lvzData.Default)
                    {
                        if (lvzData.Current == lvzData.Default)
                        {
                            // Currently using the default value and changing it to a non-default value.
                            arenaData.ExtraDifferences++;
                        }
                    }
                    else
                    {
                        if (lvzData.Current != lvzData.Default)
                        {
                            // Currently using a non-default value and changing it back to the default value.
                            arenaData.ExtraDifferences--;
                        }
                    }

                    lvzData.Current = objectChange.Data;

                    _logManager.LogA(LogLevel.Drivel, nameof(LvzObjects), arena, $"Changed object {id}. Tracking {arenaData.ExtraDifferences} changed objects.");
                }

                _network.SendToTarget(target, packetBytes, NetSendFlags.Reliable);
            }
        }

        #region Helper types

        private delegate void ChangeObjectDelegate<TState>(ref LvzObjectChange objectChange, TState state);

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
            /// Allows a player to broadcast arbitrary data to players in the same arena.
            /// </summary>
            /// <remarks>
            /// This extends the C2S 0x0A (Broadcast) to have power beyond that of manipulating Lvz objects.
            /// A player with this can send data of their choosing to players in the the same arena.
            /// The data can by anything, it is not bounded by any means. Therefore it can be very dangerous 
            /// to give the <see cref="Constants.Capabilities.BroadcastAny"/> capability.
            /// </remarks>
            Any,
        }

        public class LvzData : IResettable
        {
            public bool Off = true;

            public ObjectData Default = default;
            public ObjectData Current = default;

            bool IResettable.TryReset()
            {
                Off = true;
                Default = default;
                Current = default;
                return true;
            }
        }

        private class ArenaData : IResettable
        {
            public readonly List<LvzData> List = [];

            public int ToggleDifferences = 0;
            public int ExtraDifferences = 0;

            public readonly Lock Lock = new();

            public LvzData? GetObjectData(short objectId)
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

            public bool TryReset()
            {
                lock (Lock)
                {
                    List.Clear();
                    ToggleDifferences = 0;
                    ExtraDifferences = 0;
                }

                return true;
            }
        }

        private class PlayerData : IResettable
        {
            public BroadcastAuthorization Permission = BroadcastAuthorization.None;

            public bool TryReset()
            {
                Permission = BroadcastAuthorization.None;
                return true;
            }
        }

        #endregion
    }
}
