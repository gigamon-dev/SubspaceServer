using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// <inheritdoc cref="ICommandManager"/>
    /// </summary>
    [CoreModuleInfo]
    public class CommandManager : IModule, ICommandManager
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private InterfaceRegistrationToken _iCommandManagerToken;

        #region Helper classes

        private abstract class CommandData
        {
            public readonly Arena Arena;
            public readonly string HelpText; // TODO: change to be CommandHelpAttribute?

            public CommandData(
                Arena arena,
                string helpText)
            {
                Arena = arena;
                HelpText = helpText;
            }

            public virtual bool IsHandler(CommandDelegate handler) => false;
            public virtual bool IsHandler(CommandWithSoundDelegate handler) => false;
            public abstract void DispatchCommand(string command, string parameters, Player p, ITarget target, ChatSound sound);
        }

        private class BasicCommandData : CommandData
        {
            public readonly CommandDelegate Handler;

            public BasicCommandData(
                CommandDelegate handler,
                Arena arena,
                string helpText)
                : base(arena, helpText)
            {
                Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public override bool IsHandler(CommandDelegate handler) => Handler == handler;

            public override void DispatchCommand(string command, string parameters, Player p, ITarget target, ChatSound sound) => Handler(command, parameters, p, target);
        }

        private class SoundCommandData : CommandData
        {
            public readonly CommandWithSoundDelegate Handler;

            public SoundCommandData(
                CommandWithSoundDelegate handler,
                Arena arena,
                string helpText)
                : base(arena, helpText)
            {
                Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public override bool IsHandler(CommandWithSoundDelegate handler) => Handler == handler;

            public override void DispatchCommand(string command, string parameters, Player p, ITarget target, ChatSound sound) => Handler(command, parameters, p, target, sound);
        }

        #endregion

        private readonly Dictionary<string, LinkedList<CommandData>> _cmdLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unloggedCommands = new(StringComparer.OrdinalIgnoreCase);
        private event DefaultCommandDelegate DefaultCommandEvent;
        private readonly object _lockObj = new();

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            ILogManager logManager,
            ICapabilityManager capabilityManager,
            IConfigManager configManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            _cmdLookup.Clear();
            DefaultCommandEvent = null;
            InitializeUnloggedCommands();

            _iCommandManagerToken = _broker.RegisterInterface<ICommandManager>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<ICommandManager>(ref _iCommandManagerToken) != 0)
                return false;

            _cmdLookup.Clear();
            DefaultCommandEvent = null;

            return true;
        }

        #endregion

        #region ICommandManager Members

        void ICommandManager.AddCommand(string commandName, CommandDelegate handler, Arena arena, string helpText)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(commandName));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            AddCommand(commandName, handler, arena, helpText);
        }

        void ICommandManager.AddCommand(string commandName, CommandWithSoundDelegate handler, Arena arena, string helpText)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(commandName));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            AddCommand(commandName, handler, arena, helpText);
        }

        private void AddCommand(string commandName, Delegate handler, Arena arena, string helpText)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(commandName));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (string.IsNullOrWhiteSpace(helpText))
            {
                TryGetHelpText(commandName, handler, out helpText);
            }

            CommandData cd;
            if (handler is CommandDelegate basicHandler)
                cd = new BasicCommandData(basicHandler, arena, helpText);
            else if (handler is CommandWithSoundDelegate soundHandler)
                cd = new SoundCommandData(soundHandler, arena, helpText);
            else
                throw new ArgumentException($"Handler must be a {nameof(CommandDelegate)} or {nameof(CommandWithSoundDelegate)}.", nameof(handler));

            lock (_lockObj)
            {
                if (_cmdLookup.TryGetValue(commandName, out LinkedList<CommandData> ll) == false)
                {
                    ll = new LinkedList<CommandData>();
                    _cmdLookup.Add(commandName, ll);
                }

                ll.AddLast(cd);
            }
        }

        private bool TryGetHelpText(string commandName, Delegate handler, out string helpText)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            try
            {
                MethodInfo mi = handler.GetMethodInfo();
                foreach (CommandHelpAttribute helpAttr in mi.GetCustomAttributes<CommandHelpAttribute>())
                {
                    if (helpAttr.Command != null
                        && !string.Equals(helpAttr.Command, commandName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    StringBuilder sb = new();
                    sb.Append($"Targets: {helpAttr.Targets:F}\n");
                    sb.Append($"Args: {helpAttr.Args ?? "None" }\n");

                    if (!string.IsNullOrWhiteSpace(helpAttr.Description))
                        sb.Append(helpAttr.Description);

                    helpText = sb.ToString();
                    return true;
                }
            }
            catch (Exception ex)
            {
                // ignore any reflection errors
                _logManager.LogM(LogLevel.Drivel, nameof(CommandManager), $"Error trying to look for help attributes for command {commandName}. {ex}");
            }

            helpText = null;
            return false;
        }

        void ICommandManager.RemoveCommand(string commandName, CommandDelegate handler, Arena arena)
        {
            lock (_lockObj)
            {
                if (_cmdLookup.TryGetValue(commandName, out LinkedList<CommandData> ll) == false)
                    return;

                for (LinkedListNode<CommandData> node = ll.First; node != null; node = node.Next)
                {
                    CommandData cd = node.Value;
                    if (cd.IsHandler(handler) && cd.Arena == arena)
                    {
                        ll.Remove(node);
                        break;
                    }
                }

                if (ll.Count == 0)
                    _cmdLookup.Remove(commandName);
            }
        }

        void ICommandManager.RemoveCommand(string commandName, CommandWithSoundDelegate handler, Arena arena)
        {
            lock (_lockObj)
            {
                if (_cmdLookup.TryGetValue(commandName, out LinkedList<CommandData> ll) == false)
                    return;

                for (LinkedListNode<CommandData> node = ll.First; node != null; node = node.Next)
                {
                    CommandData cd = node.Value;
                    if (cd.IsHandler(handler) && cd.Arena == arena)
                    {
                        ll.Remove(node);
                        break;
                    }
                }

                if (ll.Count == 0)
                    _cmdLookup.Remove(commandName);
            }
        }

        event DefaultCommandDelegate ICommandManager.DefaultCommandReceived
        {
            add
            {
                lock (_lockObj)
                {
                    if (DefaultCommandEvent != null)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(CommandManager),
                            $"A {nameof(ICommandManager.DefaultCommandReceived)} is being subscribed to when one is already subscribed. " +
                            $"Check that you're not loading more than 1 module that registers a default handler (e.g. only one billing module allowed).");
                    }

                    DefaultCommandEvent += value;
                }
            }

            remove
            {
                lock (_lockObj)
                {
                    DefaultCommandEvent -= value;
                }
            }
        }

        void ICommandManager.Command(string typedLine, Player p, ITarget target, ChatSound sound)
        {
            if (string.IsNullOrEmpty(typedLine))
                return;

            if (p == null)
                return;

            // almost all commands assume that p.Arena is not null
            if (p.Arena == null)
                return;

            if (target == null)
                return;

            // NOTE: the Chat module has already removed the starting ? or * from typedLine

            bool skipLocal = false;

            typedLine = typedLine.Trim();

            // ?\<command> is a way to send a command straight to the the billing server
            if (typedLine[0] == '\\')
            {
                typedLine = typedLine.Remove(0, 1);
                if (typedLine == string.Empty)
                    return;

                skipLocal = true;
            }

            string origLine = typedLine;

            // TODO: ASSS cuts command name at 30 characters? why the limit?
            // TODO: ASSS ends command name on ' ', '=', and '#'.  Then for parameters it skips ' ' and '=', but will leave a '#' at the start in the parameters string?
            // = makes sense for ?chat=first,second,third or ?password=newpassword etc...
            // where is # used?

            string[] tokens = typedLine.Split(" =".ToCharArray(), 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd;
            string parameters;

            if (tokens.Length == 1)
            {
                cmd = tokens[0];
                parameters = string.Empty;
            }
            else if (tokens.Length == 2)
            {
                cmd = tokens[0];
                parameters = tokens[1];
            }
            else
                return;

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

            lock (_lockObj)
            {
                _cmdLookup.TryGetValue(cmd, out LinkedList<CommandData> ll);

                if (skipLocal || ll == null)
                {
                    // we don't know about this, send it to the biller
                    DefaultCommandEvent?.Invoke(cmd, origLine, p, target);
                }
                else if (Allowed(p, cmd, prefix, remoteArena))
                {
                    LogCommand(p, target, cmd, parameters);

                    foreach (CommandData cd in ll)
                    {
                        if (cd.Arena != null && cd.Arena != p.Arena)
                            continue;

                        cd.DispatchCommand(cmd, parameters, p, target, sound);
                    }
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

            lock (_lockObj)
            {
                if (_cmdLookup.TryGetValue(commandName, out LinkedList<CommandData> ll))
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

        void ICommandManager.AddUnlogged(string commandName)
        {
            lock (_lockObj)
            {
                _unloggedCommands.Add(commandName);
            }
        }

        void ICommandManager.RemoveUnlogged(string commandName)
        {
            lock (_lockObj)
            {
                _unloggedCommands.Remove(commandName);
            }
        }

        #endregion

        private bool Allowed(Player p, string cmd, string prefix, Arena remoteArena)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (string.IsNullOrEmpty(cmd))
                throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "cannot be null or empty");

            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "cannot be null or empty");

            if (_capabilityManager == null)
            {

#if ALLOW_ALL_IF_CAPMAN_IS_MISSING
                _logManager.LogM(LogLevel.Warn, nameof(CommandManager), "The capability manager isn't loaded, allowing all commands.");
                return true;
#else
                _logManager.LogM(LogLevel.Warn, nameof(CommandManager), "The capability manager isn't loaded, disallowing all commands.");
                return false;
#endif
            }

            string capability = prefix + "_" + cmd;

            if (remoteArena != null)
                return _capabilityManager.HasCapability(p, remoteArena, capability);
            else
                return _capabilityManager.HasCapability(p, capability);
        }

        private void LogCommand(Player p, ITarget target, string cmd, string parameters)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (string.IsNullOrEmpty(cmd))
                throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "cannot be null or empty");

            if (_logManager == null)
                return;

            // don't log the parameters to some commands
            if (_unloggedCommands.Contains(cmd))
                parameters = "...";

            StringBuilder sb = new(32);

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
                    sb.Append(']');
                    break;
                
                default:
                    sb.Append("(other)");
                    break;
            }

            if (!string.IsNullOrEmpty(parameters))
                _logManager.LogP(LogLevel.Info, nameof(CommandManager), p, "command {0}: {1} {2}", sb.ToString(), cmd, parameters);
            else
                _logManager.LogP(LogLevel.Info, nameof(CommandManager), p, "command {0}: {1}", sb.ToString(), cmd);

        }

        private void InitializeUnloggedCommands()
        {
            lock (_lockObj)
            {
                _unloggedCommands.Clear();

                // billing commands that shouldn't be logged
                _unloggedCommands.Add("chat");
                _unloggedCommands.Add("password");
                _unloggedCommands.Add("squadcreate");
                _unloggedCommands.Add("squadjoin");
                _unloggedCommands.Add("addop");
                _unloggedCommands.Add("adduser");
                _unloggedCommands.Add("changepassword");
                _unloggedCommands.Add("login");
                _unloggedCommands.Add("blogin");
                _unloggedCommands.Add("bpassword");
                _unloggedCommands.Add("squadpassword");
                _unloggedCommands.Add("message");
            }
        }
    }
}
