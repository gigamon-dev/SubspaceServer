using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Linq;

namespace SS.Core.Modules
{
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
        private IPlayerData _playerData;

        private InterfaceRegistrationToken<ILvzObjects> _interfaceRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

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
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _network.AddPacket(C2SPacketType.Rebroadcast, Packet_Rebroadcast);

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _commandManager.AddCommand("objon", Command_objon);
            _commandManager.AddCommand("objoff", Command_objoff);
            _commandManager.AddCommand("objset", Command_objset);

            _interfaceRegistrationToken = broker.RegisterInterface<ILvzObjects>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _interfaceRegistrationToken);

            _commandManager.RemoveCommand("objon", Command_objon);
            _commandManager.RemoveCommand("objoff", Command_objoff);
            _commandManager.RemoveCommand("objset", Command_objset);

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
        private void Command_objon(string commandName, string parameters, Player p, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                Toggle(target, id, true);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<object id>",
            Description = "Toggles the specified object off.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objoff(string commandName, string parameters, Player p, ITarget target)
        {
            if (short.TryParse(parameters, out short id) && id >= 0)
                Toggle(target, id, false);
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "[+|-]<object id 0> [+|-]<object id 1> ... [+|-]<object id N>",
            Description = "Toggles the specified objects on/off.\n" +
            "Object commands: ?objon ?objoff ?objset ?objmove ?objimage ?objlayer ?objtimer ?objmode ?objinfo ?objlist")]
        private void Command_objset(string commandName, string parameters, Player p, ITarget target)
        {
            int count = parameters.Count(c => c == '+' || c == '-');
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

        #endregion

        private void Packet_Rebroadcast(Player p, byte[] data, int length)
        {
            
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
        }

        private void ThreadPoolWork_ArenaActionWork(Arena arena)
        {
            foreach (LvzFileInfo fileInfo in _mapData.LvzFilenames(arena))
            {
                // TODO: 
                //fileInfo.Filename
            }

            _arenaManager.UnholdArena(arena);
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                    return;

                if (_capabilityManager.HasCapability(p, Constants.Capabilities.BroadcastAny))
                    pd.Permission = BroadcastAccess.Any;
                else if (_capabilityManager.HasCapability(p, Constants.Capabilities.BroadcastBot))
                    pd.Permission = BroadcastAccess.Bot;
            }
            else if(action == PlayerAction.EnterGame)
            {
                SendState(p);
            }
        }

        private void SendState(Player player)
        {
            
        }

        private void Toggle(ITarget target, short id, bool isEnabled)
        {
            if (id < 0)
                throw new ArgumentOutOfRangeException(nameof(id), "Cannot be negative.");

            Span<byte> packet = stackalloc byte[3];
            packet[0] = (byte)S2CPacketType.ToggleObj;
            BinaryPrimitives.WriteInt16LittleEndian(
                packet[1..],
                (short)(id & 0x7FF | (isEnabled ? 0x0000 : 0x8000)));

            _network.SendToTarget(target, packet, NetSendFlags.Reliable);

            if (target.TryGetArenaTarget(out Arena arena)
                && arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                lock (ad.Lock)
                {
                    // TOOD: 
                }
            }
        }

        private void ToggleSet(ITarget target, ReadOnlySpan<LvzToggle> set)
        {
            // Maximum Continuum allows at once.
            if (set.Length > 1023) // TODO: loop intead filling up to the max on each iteration
                throw new ArgumentOutOfRangeException(nameof(set), "Length was > 1023.");

            int packetLength = 1 + 2 * set.Length;

            byte[] byteArray = null;
            if (packetLength > Constants.MaxPacket)
                byteArray = ArrayPool<byte>.Shared.Rent(packetLength);

            try
            {
                Span<byte> packet = byteArray != null ? byteArray : stackalloc byte[packetLength];
                packet[0] = (byte)S2CPacketType.ToggleObj;

                for (int i = 0; i < set.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        packet.Slice(1 + 2 * i, 2),
                        (short)(set[i].Id & 0x7FF | (set[i].IsEnabled ? 0x0000 : 0x8000)));

                    if (target.TryGetArenaTarget(out Arena arena)
                        && arena.TryGetExtraData(_adKey, out ArenaData ad))
                    {
                        lock (ad.Lock)
                        {
                            // TODO:
                        }
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

        #region Helper types

        private enum BroadcastAccess
        {
            None,
            Bot,
            Any,
        }

        //public struct LvzData
        //{

        //}

        private class ArenaData
        {
            //List<LvzData> list
            //uint toggleDiffs;
            //uint extDiffs;

            public readonly object Lock = new();
        }

        private class PlayerData
        {
            public BroadcastAccess Permission = BroadcastAccess.None;
        }

        #endregion
    }
}
