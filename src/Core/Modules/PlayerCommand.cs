using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class PlayerCommand : IModule
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IChat _chat;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private ICommandManager _commandManager;
        private IGame _game;
        IGroupManager _groupManager;
        private IMainloop _mainloop;
        private IMapData _mapData;
        private IModuleManager _mm;
        private INetwork _net;
        private IPlayerData _playerData;

        private DateTime _startedAt;

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            ICapabilityManager capabilityManager,
            IConfigManager configManager,
            ICommandManager commandManager,
            IGame game,
            IGroupManager groupManager,
            IMainloop mainloop,
            IMapData mapData,
            IModuleManager mm,
            INetwork net, 
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _mm = mm ?? throw new ArgumentNullException(nameof(mm));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _startedAt = DateTime.UtcNow;

            // TODO: do some sort of derivative of that command group thing asss does
            _commandManager.AddCommand("arena", Command_arena);
            _commandManager.AddCommand("shutdown", Command_shutdown);
            _commandManager.AddCommand("recyclezone", Command_recyclezone);
            _commandManager.AddCommand("uptime", Command_uptime);
            _commandManager.AddCommand("version", Command_version);
            _commandManager.AddCommand("sheep", Command_sheep);
            _commandManager.AddCommand("netstats", Command_netstats);
            _commandManager.AddCommand("info", Command_info);
            _commandManager.AddCommand("lsmod", Command_lsmod);
            _commandManager.AddCommand("modinfo", Command_modinfo);
            _commandManager.AddCommand("insmod", Command_insmod);
            _commandManager.AddCommand("rmmod", Command_rmmod);
            _commandManager.AddCommand("attmod", Command_attmod);
            _commandManager.AddCommand("detmod", Command_detmod);
            _commandManager.AddCommand("getgroup", Command_getgroup);
            _commandManager.AddCommand("grplogin", Command_grplogin);
            _commandManager.AddCommand("where", Command_where);
            _commandManager.AddCommand("mapinfo", Command_mapinfo);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand("arena", Command_arena);
            _commandManager.RemoveCommand("shutdown", Command_shutdown);
            _commandManager.RemoveCommand("recyclezone", Command_recyclezone);
            _commandManager.RemoveCommand("uptime", Command_uptime);
            _commandManager.RemoveCommand("version", Command_version);
            _commandManager.RemoveCommand("sheep", Command_sheep);
            _commandManager.RemoveCommand("netstats", Command_netstats);
            _commandManager.RemoveCommand("info", Command_info);
            _commandManager.RemoveCommand("lsmod", Command_lsmod);
            _commandManager.RemoveCommand("modinfo", Command_modinfo);
            _commandManager.RemoveCommand("insmod", Command_insmod);
            _commandManager.RemoveCommand("rmmod", Command_rmmod);
            _commandManager.RemoveCommand("attmod", Command_attmod);
            _commandManager.RemoveCommand("detmod", Command_detmod);
            _commandManager.RemoveCommand("getgroup", Command_getgroup);
            _commandManager.RemoveCommand("grplogin", Command_grplogin);
            _commandManager.RemoveCommand("where", Command_where);
            _commandManager.RemoveCommand("mapinfo", Command_mapinfo);

            return true;
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-a}] [{-t}]",
            Description = 
            "Lists the available arenas. Specifying {-a} will also include\n" +
            "empty arenas that the server knows about. The {-t} switch forces\n" +
            "the output to be in text even for regular clients (useful when using\n" +
            "the Continuum chat window).")]
        private void Command_arena(string command, string parameters, Player p, ITarget target)
        {
            // TODO: add support for chat output
            // TODO: add support for -a argument
            //bool isChatOutput = p.Type == ClientType.Chat || parameters.Contains("-t");
            bool includePrivateArenas = _capabilityManager.HasCapability(p, Constants.Capabilities.SeePrivArena);

            Span<byte> bufferSpan = stackalloc byte[1024];

            // Write header
            bufferSpan[0] = (byte)S2CPacketType.Arena;
            int length = 1;

            _arenaManager.Lock();

            try
            {
                // refresh arena counts
                _arenaManager.GetPopulationSummary(out _, out _);

                foreach (Arena arena in _arenaManager.ArenaList)
                {
                    int nameLength = arena.Name.Length + 1; // +1 because the name in the packet is null terminated
                    int additionalLength = nameLength + 2;

                    if (length + additionalLength > (Constants.MaxPacket - Constants.ReliableHeaderLen))
                    {
                        break;
                    }

                    if (arena.Status == ArenaState.Running
                        && (!arena.IsPrivate || includePrivateArenas || p.Arena == arena))
                    {
                        // arena name
                        Span<byte> remainingSpan = bufferSpan.Slice(length);
                        remainingSpan.Slice(remainingSpan.WriteNullTerminatedASCII(arena.Name));

                        // player count (a negative value denotes the player's current arena)
                        Span<byte> playerCountSpan = remainingSpan.Slice(nameLength, 2);
                        BinaryPrimitives.WriteInt16LittleEndian(playerCountSpan, arena == p.Arena ? (short)-arena.Total : (short)arena.Total);

                        length += additionalLength;
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            // TODO: additional arena list logic (-a argument)

            _net.SendToOne(p, bufferSpan.Slice(0, length), NetSendFlags.Reliable);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-r}]",
            Description = 
            "Immediately shuts down the server, exiting with {EXIT_NONE}. If\n" + 
            "{-r} is specified, exit with {EXIT_RECYCLE} instead. The {run-asss}\n" +
            "script, if it is being used, will notice {EXIT_RECYCLE} and restart\n" +
            "the server.\n")]
        private void Command_shutdown(string command, string parameters, Player p, ITarget target)
        {
            ExitCode code = string.Equals(parameters, "-r", StringComparison.OrdinalIgnoreCase)
                ? ExitCode.Recycle
                : ExitCode.None;

            _mainloop.Quit(code);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description =
            "Immediately shuts down the server, exiting with {EXIT_RECYCLE}. The " +
            "{run-asss} script, if it is being used, will notice {EXIT_RECYCLE} " +
            "and restart the server.\n")]
        private void Command_recyclezone(string command, string parameters, Player p, ITarget target)
        {
            _mainloop.Quit(ExitCode.Recycle);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays how long the server has been running.")]
        private void Command_uptime(string command, string parameters, Player p, ITarget target)
        {
            TimeSpan ts = DateTime.UtcNow - _startedAt;

            _chat.SendMessage(p, "uptime: {0} days {1} hours {2} minutes {3} seconds", ts.Days, ts.Hours, ts.Minutes, ts.Seconds);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-v}]",
            Description = 
            "Prints out information about the server." +
            "For staff members, it will print more detailed version information." +
            "If staff members specify the {-v} arg, it will print even more verbose information.")]
        private void Command_version(string command, string parameters, Player p, ITarget target)
        {
            _chat.SendMessage(p, $"Subspace Server .NET");

            if (_capabilityManager.HasCapability(p, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(p, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!parameters.Contains("v")
                        && (assembly.FullName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("netstandard,", StringComparison.OrdinalIgnoreCase)
                        ))
                    {
                        continue;
                    }

                    _chat.SendMessage(p, $"{assembly.FullName}");
                }
            }
        }

        private void Command_sheep(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Arena)
                return;

            string sheepMessage = _configManager.GetStr(p.Arena.Cfg, "Misc", "SheepMessage");

            if (sheepMessage != null)
                _chat.SendSoundMessage(p, ChatSound.Sheep, sheepMessage);
            else
                _chat.SendSoundMessage(p, ChatSound.Sheep, "Sheep successfully cloned -- hello Dolly");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Prints out some statistics from the network layer.")]
        private void Command_netstats(string command, string parameters, Player p, ITarget target)
        {
            ulong secs = Convert.ToUInt64((DateTime.UtcNow - _startedAt).TotalSeconds);

            IReadOnlyNetStats stats = _net.GetStats();
            _chat.SendMessage(p, "netstats: pings={0}  pkts sent={1}  pkts recvd={2}", 
                stats.PingsReceived, stats.PacketsSent, stats.PacketsReceived);

            ulong bwout = (stats.BytesSent + stats.PacketsSent * 28) / secs; // TODO: figure out why 28?
            ulong bwin = (stats.BytesReceived + stats.PacketsReceived * 28) / secs;
            _chat.SendMessage(p, "netstats: bw out={0}  bw in={1}", bwout, bwin);

            _chat.SendMessage(p, "netstats: buffers used={0}/{1} ({2:p})",
                stats.BuffersUsed, stats.BuffersTotal, (double)stats.BuffersUsed / (double)stats.BuffersTotal);

            _chat.SendMessage(p, "netstats: grouped={0}/{1}/{2}/{3}/{4}/{5}/{6}/{7}",
                stats.GroupedStats[0],
                stats.GroupedStats[1],
                stats.GroupedStats[2],
                stats.GroupedStats[3],
                stats.GroupedStats[4],
                stats.GroupedStats[5],
                stats.GroupedStats[6],
                stats.GroupedStats[7]);

            _chat.SendMessage(p, "netstats: pri={0}/{1}/{2}/{3}/{4}",
                stats.PriorityStats[0],
                stats.PriorityStats[1],
                stats.PriorityStats[2],
                stats.PriorityStats[3],
                stats.PriorityStats[4]);
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description =
            "Displays various information on the target player, including which\n" +
            "client they are using, their resolution, IP address, how long they have\n" +
            "been connected, and bandwidth usage information.")]
        private void Command_info(string command, string parameters, Player p, ITarget target)
        {
            if (target == null 
                || target.Type != TargetType.Player
                || !(target is IPlayerTarget playerTarget))
            {
                _chat.SendMessage(p, "info: must use on a player");
                return;
            }

            Player t = playerTarget.Player;
            if (t == null)
                return;

            string prefix = t.Name;
            TimeSpan connectedTimeSpan = DateTime.UtcNow - t.ConnectTime;

            _chat.SendMessage(p, $"{prefix}: pid={t.Id} name='{t.Name}' squad='{t.Squad}' " +
                $"auth={(t.Flags.Authenticated?'Y':'N')} ship={t.Ship} freq={t.Freq}");
            _chat.SendMessage(p, $"{prefix}: arena={(t.Arena != null ? t.Arena.Name : "(none)")} " +
                $"client={t.ClientName} res={t.Xres}x{t.Yres} onFor={connectedTimeSpan} " +
                $"connectAs={(!string.IsNullOrWhiteSpace(t.ConnectAs)? t.ConnectAs : "<default>")}");

            if (t.IsStandard)
            {
                SendCommonBandwidthInfo(p, t, connectedTimeSpan, prefix, true);
            }
        }

        private void SendCommonBandwidthInfo(Player p, Player t, TimeSpan connectedTimeSpan, string prefix, bool includeSensitive)
        {
            NetClientStats stats = _net.GetClientStats(t);

            if (includeSensitive)
            {
                _chat.SendMessage(p, $"{prefix}: ip:{stats.IPEndPoint.Address} port:{stats.IPEndPoint.Port} " +
                    $"encName={stats.EncryptionName} macId={t.MacId} permId={t.PermId}");
            }

            int ignoringwpns = (int)(100f * _game.GetIgnoreWeapons(t));

            _chat.SendMessage(p,
                $"{prefix}: " +
                $"ave bw in/out={(stats.BytesReceived / connectedTimeSpan.TotalSeconds):N0}/{(stats.BytesSent / connectedTimeSpan.TotalSeconds):N0} " +
                $"ignoringWpns={ignoringwpns} dropped={stats.PacketsDropped}");

            // TODO: bwlimit info

            if (t.Flags.NoShip)
                _chat.SendMessage(p, $"{prefix}: lag too high to play");

            if (t.Flags.NoFlagsBalls)
                _chat.SendMessage(p, $"{prefix}: lag too high to carry flags or balls");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-a}] [{-s}] [<text>]",
            Description =
            "Lists all the modules currently loaded into the server. With {-a}, lists\n" +
            "only modules attached to this arena. With {-s}, sorts by name.\n" +
            "With optional `text`, limits modules displayed to those whose names\n" +
            "contain the given text.")]
        private void Command_lsmod(string command, string parameters, Player p, ITarget target)
        {
            bool sort = false;
            string substr = null;

            Arena arena = null;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string[] args = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach(string arg in args)
                {
                    if (string.Equals(arg, "-a", StringComparison.OrdinalIgnoreCase))
                    {
                        if (target.Type == TargetType.Arena
                            && target is IArenaTarget arenaTarget)
                        {
                            arena = arenaTarget.Arena;
                        }
                    }
                    else if (string.Equals(arg, "-s", StringComparison.OrdinalIgnoreCase))
                    {
                        sort = true;
                    }
                    else
                    {
                        substr = arg;
                    }
                }
            }

            LinkedList<string> modulesList = new LinkedList<string>();

            _mm.EnumerateModules(
                (moduleType, _) =>
                {
                    string name = moduleType.FullName;
                    if (substr == null || name.Contains(substr))
                        modulesList.AddLast(name);
                },
                arena);

            IEnumerable<string> modules = modulesList;

            if (sort)
            {
                modules = from str in modules
                          orderby str
                          select str;
            }

            StringBuilder sb = new StringBuilder();
            foreach (string str in modules)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                
                sb.Append(str);
            }

            _chat.SendWrappedText(p, sb.ToString());
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description =
            "Displays information about the specified module. This might include a\n" +
            "version number, contact information for the author, and a general\n" +
            "description of the module.\n")]
        private void Command_modinfo(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            var infoArray = _mm.GetModuleInfo(parameters);

            if (infoArray.Length > 0)
            {
                foreach(var info in infoArray)
                {
                    _chat.SendMessage(p, info.ModuleTypeName);
                    _chat.SendMessage(p, $"  Type: {info.ModuleQualifiedName}");
                    _chat.SendMessage(p, $"  Assembly Path: {info.AssemblyPath}");
                    _chat.SendMessage(p, $"  Module Type: {(info.IsPlugin ? "plug-in" : "built-in")}");

                    if (info.AttachedArenas.Length > 0)
                    {
                        string attachedArenas = string.Join(", ", info.AttachedArenas.Select(a => a.Name));
                        _chat.SendMessage(p, $"  Attached Arenas: {attachedArenas}");
                    }

                    _chat.SendMessage(p, $"  Description:");
                    string[] tokens = info.Description.Split('\n');
                    foreach (string token in tokens)
                    {
                        _chat.SendMessage(p, $"    {token}");
                    }
                }
            }
            else
            {
                _chat.SendMessage(p, $"Module '{parameters}' not found.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Immediately loads the specified module into the server.")]
        private void Command_insmod(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            string moduleTypeName;
            string path;

            string[] tokens = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1)
            {
                moduleTypeName = tokens[0];
                path = null;
            }
            else if(tokens.Length == 2)
            {
                moduleTypeName = tokens[0];
                path = tokens[1];
                path = path.Trim('"', '\'', ' ');
            }
            else
            {
                return;
            }

            if (_mm.LoadModule(moduleTypeName, path))
                _chat.SendMessage(p, $"Module '{moduleTypeName}' loaded.");
            else
                _chat.SendMessage(p, $"Failed to load module '{moduleTypeName}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Attempts to unload the specified module from the server.")]
        private void Command_rmmod(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            if (_mm.UnloadModule(parameters))
                _chat.SendMessage(p, $"Module '{parameters}' unloaded.");
            else
                _chat.SendMessage(p, $"Failed to unload module '{parameters}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-d}] <module name>",
            Description =
            "Attaches the specified module to this arena. Or with {-d},\n" +
            "detaches the module from the arena.")]
        private void Command_attmod(string command, string parameters, Player p, ITarget target)
        {
            bool detach = false;
            string module = parameters;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string[] tokens = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 2 && string.Equals(tokens[0], "-d"))
                {
                    detach = true;
                    module = tokens[1];
                }
            }

            AttachDetachModule(p, module, detach);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Detaches the module from the arena.")]
        private void Command_detmod(string command, string parameters, Player p, ITarget target)
        {
            AttachDetachModule(p, parameters, true);
        }

        private void AttachDetachModule(Player p, string module, bool detach)
        {
            if (p == null)
                return;

            if (detach)
            {
                if (_mm.DetachModule(module, p.Arena))
                    _chat.SendMessage(p, $"Module '{module}' detached.");
                else
                    _chat.SendMessage(p, $"Failed to detach module '{module}'.");
            }
            else
            {
                if (_mm.AttachModule(module, p.Arena))
                    _chat.SendMessage(p, $"Module '{module}' attached.");
                else
                    _chat.SendMessage(p, $"Failed to attach module '{module}'.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description = "Displays the group of the player, or if none specified, you.")]
        private void Command_getgroup(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type == TargetType.Player && target is IPlayerTarget playerTarget)
            {
                Player targetPlayer = playerTarget.Player;
                _chat.SendMessage(p, $"{targetPlayer.Name} is in group {_groupManager.GetGroup(targetPlayer)}.");
            }
            else
            {
                _chat.SendMessage(p, $"You are in group {_groupManager.GetGroup(p)}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<group name> <password>",
            Description = "Logs you in to the specified group, if the password is correct.")]
        private void Command_grplogin(string command, string parameters, Player p, ITarget target)
        {            
            string[] parameterArray = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parameterArray.Length != 2)
            {
                _chat.SendMessage(p, "You must specify a group name and password.");
            }
            else if (_groupManager.CheckGroupPassword(parameterArray[0], parameterArray[1]))
            {
                _groupManager.SetTempGroup(p, parameterArray[0]);
                _chat.SendMessage(p, $"You are now in group {parameterArray[0]}.");
            }
            else
            {
                _chat.SendMessage(p, $"Bad password for group {parameterArray[0]}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = "Displays the current location (on the map) of the target player.")]
        private void Command_where(string command, string parameters, Player p, ITarget target)
        {
            Player t = null;

            if (target != null
                && target.Type == TargetType.Player
                && target is IPlayerTarget playerTarget)
            {
                t = playerTarget.Player;
            }

            if (t == null)
                t = p;

            // right shift by 4 is divide by 16 (each tile is 16 pixels)
            int x = t.Position.X >> 4;
            int y = t.Position.Y >> 4;

            string name = (t == p) ? "You" : t.Name;
            string verb = (t == p) ? "are" : "is";

            if (t.IsStandard)
            {
                _chat.SendMessage(p, $"{name} {verb} at {(char)('A' + (x * 20 / 1024))}{(y * 20 / 1024 + 1)} ({x},{y})");
            }
            else
            {
                _chat.SendMessage(p, $"{name} {verb} not using a playable client.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena, 
            Args = null,
            Description = "Displays information about the map in this arena.")]
        private void Command_mapinfo(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            string fileName = _mapData.GetMapFilename(arena, null);

            _chat.SendMessage(p, "LVL file loaded from '{0}'.", !string.IsNullOrWhiteSpace(fileName) ? fileName : "<nowhere>");

            const string NotSet = "<not set>";

            _chat.SendMessage(p, 
                $"name: {_mapData.GetAttribute(arena, MapAttributeKeys.Name) ?? NotSet}, " +
                $"version: {_mapData.GetAttribute(arena, MapAttributeKeys.Version) ?? NotSet}");

            _chat.SendMessage(p,
                $"map creator: {_mapData.GetAttribute(arena, MapAttributeKeys.MapCreator) ?? NotSet}, " +
                $"tileset creator: {_mapData.GetAttribute(arena, MapAttributeKeys.TilesetCreator) ?? NotSet}, " +
                $"program: {_mapData.GetAttribute(arena, MapAttributeKeys.Program) ?? NotSet}");

            var errors = _mapData.GetErrors(arena);

            _chat.SendMessage(p, 
                $"tiles:{_mapData.GetTileCount(arena)} " +
                $"flags:{_mapData.GetFlagCount(arena)} " + 
                $"regions:{_mapData.GetRegionCount(arena)} " +
                $"errors:{errors.Count}");

            if (errors.Count > 0 && _capabilityManager.HasCapability(p, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(p, "Error details:");
                foreach (var error in errors)
                {
                    _chat.SendMessage(p, $"- {error}");
                }
            }

            // TODO: estimated memory stats
        }
    }
}
