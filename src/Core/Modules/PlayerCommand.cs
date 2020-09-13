using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentInterfaces;
using System.Reflection;
using System.Xml.Schema;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class PlayerCommand : IModule
    {
        private ComponentBroker _broker;
        private IModuleManager _mm;
        private IPlayerData _playerData;
        private IChat _chat;
        private ICommandManager _commandManager;
        //private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        //private IConfigManager _configManager;
        private INetwork _net;

        private DateTime _startedAt;

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IModuleManager mm,
            IPlayerData playerData,
            IChat chat,
            ICommandManager commandManager,
            ICapabilityManager capabilityManager,
            INetwork net)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _mm = mm ?? throw new ArgumentNullException(nameof(mm));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _net = net ?? throw new ArgumentNullException(nameof(net));

            _startedAt = DateTime.Now;

            // TODO: do some sort of derivative of that command group thing asss does

            _commandManager.AddCommand("uptime", command_uptime, null, 
@"Targets: none
Args: none
Displays how long the server has been running.");

            _commandManager.AddCommand("version", command_version, null, 
@"Targets: none
Args: none
Displays version information about the server. It might also print out some information about the machine that it's running on.");

            _commandManager.AddCommand("sheep", command_sheep, null, null);
            _commandManager.AddCommand("netstats", Command_netstats, null, null);

            _commandManager.AddCommand("lsmod", Command_lsmod, null, null);
            _commandManager.AddCommand("modinfo", Command_modinfo, null, null);
            _commandManager.AddCommand("insmod", Command_insmod, null, null);
            _commandManager.AddCommand("rmmod", Command_rmmod, null, null);
            _commandManager.AddCommand("attmod", Command_attmod, null, null);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            // TODO: 
            //_commandManager.RemoveCommand()
            return true;
        }

        #endregion

        private void command_uptime(string command, string parameters, Player p, ITarget target)
        {
            TimeSpan ts = DateTime.Now - _startedAt;

            _chat.SendMessage(p, "uptime: {0} days {1} hours {2} minutes {3} seconds", ts.Days, ts.Hours, ts.Minutes, ts.Seconds);
        }

        private void command_version(string command, string parameters, Player p, ITarget target)
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

        private void command_sheep(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Arena)
                return;

            string sheepMessage = null;//_configManager.

            if (sheepMessage != null)
                _chat.SendSoundMessage(p, ChatSound.Sheep, sheepMessage);
            else
                _chat.SendSoundMessage(p, ChatSound.Sheep, "Sheep successfully cloned -- hello Dolly");
        }

        private void Command_netstats(string command, string parameters, Player p, ITarget target)
        {
            ulong secs = Convert.ToUInt64((DateTime.Now - _startedAt).TotalSeconds);

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

        private void Command_rmmod(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            if (_mm.UnloadModule(parameters))
                _chat.SendMessage(p, $"Module '{parameters}' unloaded.");
            else
                _chat.SendMessage(p, $"Failed to unload module '{parameters}'.");
        }

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

            if (detach)
            {
                if(_mm.DetachModule(module, p.Arena))
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
    }
}
