using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class CommandManager : IModule, ICommandManager
    {
        private ModuleManager _mm;
        private IPlayerData _playerData;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;

        private class CommandData
        {
            public readonly CommandDelegate Handler;
            public readonly Arena Arena;
            public readonly string HelpText;

            public CommandData(
                CommandDelegate handler,
                Arena arena,
                string helpText)
            {
                if (handler == null)
                    throw new ArgumentNullException("handler");

                Handler = handler;
                Arena = arena;
                HelpText = helpText;
            }
        }

        private Dictionary<string, LinkedList<CommandData>> _cmdLookup;
        private object _cmdmtx = new object();

        private CommandDelegate _defaultHandler;

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(ILogManager), 
                    typeof(ICapabilityManager), 
                    typeof(IConfigManager), 
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _capabilityManager = interfaceDependencies[typeof(ICapabilityManager)] as ICapabilityManager;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;

            _cmdLookup = new Dictionary<string, LinkedList<CommandData>>(StringComparer.OrdinalIgnoreCase);
            _defaultHandler = null;

            _mm.RegisterInterface<ICommandManager>(this);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _mm.UnregisterInterface<ICommandManager>();

            return true;
        }

        #endregion

        #region ICommandManager Members

        void ICommandManager.AddCommand(string commandName, CommandDelegate handler, Arena arena, string helptext)
        {
            if (commandName == null)
                _defaultHandler = handler;
            else
            {
                CommandData cd = new CommandData(handler, arena, helptext);

                lock (_cmdmtx)
                {
                    LinkedList<CommandData> ll;
                    if (_cmdLookup.TryGetValue(commandName, out ll) == false)
                    {
                        ll = new LinkedList<CommandData>();
                        _cmdLookup.Add(commandName, ll);
                    }
                    ll.AddLast(cd);
                }
            }
        }

        void ICommandManager.RemoveCommand(string commandName, CommandDelegate handler, Arena arena)
        {
            if (commandName == null)
            {
                if (_defaultHandler == handler)
                    _defaultHandler = null;
            }
            else
            {
                LinkedList<CommandData> ll;

                lock (_cmdmtx)
                {
                    if (_cmdLookup.TryGetValue(commandName, out ll) == false)
                        return;

                    for (LinkedListNode<CommandData> node = ll.First; node != null; node = node.Next)
                    {
                        CommandData cd = node.Value;
                        if (cd.Handler == handler && cd.Arena == arena)
                        {
                            ll.Remove(node);
                            break;
                        }
                    }

                    if (ll.Count == 0)
                        _cmdLookup.Remove(commandName);
                }
            }
        }

        void ICommandManager.Command(string typedLine, Player p, ITarget target, ChatSound sound)
        {
            if (string.IsNullOrEmpty(typedLine))
                return;

            // almost all commands assume that p.Arena is not null
            if (p.Arena == null)
                return;

            bool skipLocal = false;

            typedLine = typedLine.Trim();

            if (typedLine[0] == '\\')
            {
                typedLine = typedLine.Remove(0, 1);
                if(typedLine == string.Empty)
                    return;

                skipLocal = true;
            }

            string origLine = typedLine;

            
            string[] tokens = typedLine.Split(" =".ToCharArray(), 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd; // TODO: handle 30 character max on command name?
            string parameters;

            if(tokens.Length == 1)
            {
                cmd = tokens[0];
                parameters = string.Empty;
            }
            else if(tokens.Length == 2)
            {
                cmd = tokens[0];
                parameters = tokens[1];
            }
            else
                return;

            string cmdWithSound = cmd + '\0' + (char)sound;
            /*
            // find end of command
            int charactersToSearch = typedLine.Length;
            if(charactersToSearch > 30)
                charactersToSearch = 30; // max of 30 characters (might be a biller protocol limitation?)
            int index = typedLine.IndexOfAny(" =".ToCharArray(), 0, charactersToSearch);
            
            string cmd;
            string parameters;
            
            if(index == -1)
            {
                cmd = typedLine;
                parameters = string.Empty;
            }
            else
            {
                cmd = typedLine.Substring(0, index);
                parameters = typedLine.Substring(index
            }
            */
            
            /*
            int index = 0;
            while (index < sb.Length && sb[index] != ' ' && sb[index] != '=' && index < 30)
                index++;

            if(index == 0)
                return;

            string cmd = sb.ToString(0, index);
            string cmdWithSound = cmd + '\0' + (char)sound;

            while (index < sb.Length && sb[index] != ' ' && sb[index] != '=')
                index++;

            string parameters;
            if (index == sb.Length - 1)
                parameters = string.Empty;
            else
                parameters = sb.ToString(index, sb.Length - index
            */

            string prefix;
            Arena remoteArena = null;

            if (target.Type == TargetType.Arena || target.Type == TargetType.None)
                prefix = "cmd";
            else if (target.Type == TargetType.Player)
            {
                IPlayerTarget pt = (IPlayerTarget)target;
                if (pt.Player.Arena == p.Arena)
                    prefix = "privcmd";
                else
                {
                    remoteArena = pt.Player.Arena;
                    prefix = "rprivcmd";
                }
            }
            else
                prefix = "privcmd";

            lock (_cmdmtx)
            {
                LinkedList<CommandData> ll;
                _cmdLookup.TryGetValue(cmd, out ll);

                if (skipLocal || ll == null)
                {
                    // we don't know about this, send it to the biller
                    if (_defaultHandler != null)
                        _defaultHandler(cmd, origLine, p, target);
                }
                else if (allowed(p, cmd, prefix, remoteArena))
                {
                    foreach (CommandData cd in ll)
                    {
                        if (cd.Arena != null && cd.Arena != p.Arena)
                            continue;

                        cd.Handler(cmd, parameters, p, target);
                    }

                    logCommand(p, target, cmd, parameters);
                }
#if CFG_LOG_ALL_COMMAND_DENIALS
                else
                {
                    _logManager.LogP(LogLevel.Drivel, "CommandManager", p, "permission denied for {0}", cmd);
                }
#endif
            }
        }

        string ICommandManager.GetHelpText(string commandName, Arena arena)
        {
            string ret = null;
            LinkedList<CommandData> ll;

            lock (_cmdmtx)
            {
                if (_cmdLookup.TryGetValue(commandName, out ll))
                {
                    for (LinkedListNode<CommandData> node = ll.First; node != null; node = node.Next)
                    {
                        CommandData cd = node.Value;
                        if (cd.Arena == null || cd.Arena == arena)
                        {
                            ret = cd.HelpText;
                        }
                    }
                }
            }

            return ret;
        }

        #endregion

        private bool allowed(Player p, string cmd, string prefix, Arena remoteArena)
        {
            if (p == null)
                throw new ArgumentNullException("p");

            if (string.IsNullOrEmpty(cmd))
                throw new ArgumentOutOfRangeException("cmd", cmd, "cannot be null or empty");

            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentOutOfRangeException("prefix", prefix, "cannot be null or empty");

            if (_capabilityManager == null)
            {
                
#if ALLOW_ALL_IF_CAPMAN_IS_MISSING
                _logManager.Log(LogLevel.Warn, "<cmdman> the capability manager isn't loaded, allowing all commands");
                return true;
#else
                _logManager.Log(LogLevel.Warn, "<cmdman> the capability manager isn't loaded, disallowing all commands");
                return false;
#endif
            }

            string capability = prefix + "_" + cmd;

            if (remoteArena != null)
                return _capabilityManager.HasCapability(p, remoteArena, capability);
            else
                return _capabilityManager.HasCapability(p, capability);
        }

        private void logCommand(Player p, ITarget target, string cmd, string parameters)
        {
            if (p == null)
                throw new ArgumentNullException("p");

            if (target == null)
                throw new ArgumentNullException("target");

            if (string.IsNullOrEmpty(cmd))
                throw new ArgumentOutOfRangeException("cmd", cmd, "cannot be null or empty");

            if (_logManager == null)
                return;

            // don't log the parameters to some commands
            if (dontLog(cmd))
                parameters = "...";

            StringBuilder sb = new StringBuilder(32);

            switch (target.Type)
            {
                case TargetType.Arena:
                    sb.Append("(arena)");
                    break;

                case TargetType.Freq:
                    sb.Append("(freq ");
                    sb.Append((target as ITeamTarget).Freq);
                    sb.Append(')');
                    break;

                case TargetType.Player:
                    sb.Append("to [");
                    sb.Append((target as IPlayerTarget).Player.Name);
                    sb.Append("]");
                    break;
                
                default:
                    sb.Append("(other)");
                    break;
            }

            if (!string.IsNullOrEmpty(parameters))
                _logManager.LogP(LogLevel.Info, "CommandManager", p, "command {0}: {1} {2}", sb.ToString(), cmd, parameters);
            else
                _logManager.LogP(LogLevel.Info, "CommandManager", p, "command {0}: {1}", sb.ToString(), cmd);

        }

        private bool dontLog(string cmd)
        {
            if (string.Compare(cmd, "chat", true) == 0) return true;
            if (string.Compare(cmd, "password", true) == 0) return true;
            if (string.Compare(cmd, "passwd", true) == 0) return true;
            if (string.Compare(cmd, "local_password", true) == 0) return true;
            if (string.Compare(cmd, "squadcreate", true) == 0) return true;
            if (string.Compare(cmd, "squadjoin", true) == 0) return true;
            if (string.Compare(cmd, "addop", true) == 0) return true;
            if (string.Compare(cmd, "adduser", true) == 0) return true;
            if (string.Compare(cmd, "changepassword", true) == 0) return true;
            if (string.Compare(cmd, "login", true) == 0) return true;
            if (string.Compare(cmd, "blogin", true) == 0) return true;
            if (string.Compare(cmd, "bpassword", true) == 0) return true;

            return false;
        }
    }
}
