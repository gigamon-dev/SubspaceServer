using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// <inheritdoc cref="ICommandManager"/>
    /// </summary>
    [CoreModuleInfo]
    public class CommandManager : IModule, ICommandManager, IModuleLoaderAware
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private InterfaceRegistrationToken _iCommandManagerToken;

        private IChat _chat;

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
        }

        #endregion

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
        private readonly Dictionary<string, LinkedList<CommandData>> _cmdLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unloggedCommands = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _lockObj = new();
        private event DefaultCommandDelegate _defaultCommandEvent;

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

            _rwLock.EnterWriteLock();

            try
            {
                _cmdLookup.Clear();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            InitializeUnloggedCommands();

            lock (_lockObj)
            {
                _defaultCommandEvent = null;
            }

            // In ASSS, ?commands and ?allcommands are implemented in a separate module, cmdlist.
            // To simplify (not having to pass a collection of data over), it's implemented here, in the CommandManager.
            // However, this complicates dependencies.  The Chat module has a dependency on ICommandManager.
            // To prevent cyclical dependencies, CommandManager doesn't require IChat to Load
            // and instead gets IChat later using IModuleLoaderAware.PostLoad.
            ((ICommandManager)this).AddCommand("commands", Command_commands, null, null);
            ((ICommandManager)this).AddCommand("allcommands", Command_allcommands, null, null);

            _iCommandManagerToken = _broker.RegisterInterface<ICommandManager>(this);

            return true;
        }

        public bool PostLoad(ComponentBroker broker)
        {
            _chat = broker.GetInterface<IChat>();
            return true;
        }

        public bool PreUnload(ComponentBroker broker)
        {
            broker.ReleaseInterface(ref _chat);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<ICommandManager>(ref _iCommandManagerToken) != 0)
                return false;

            _rwLock.EnterWriteLock();

            try
            {
                _cmdLookup.Clear();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            lock (_lockObj)
            {
                _defaultCommandEvent = null;
            }

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

            _rwLock.EnterWriteLock();

            try
            {
                if (_cmdLookup.TryGetValue(commandName, out LinkedList<CommandData> ll) == false)
                {
                    ll = new LinkedList<CommandData>();
                    _cmdLookup.Add(commandName, ll);
                }

                ll.AddLast(cd);
            }
            finally
            {
                _rwLock.ExitWriteLock();
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
            _rwLock.EnterWriteLock();

            try
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
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        void ICommandManager.RemoveCommand(string commandName, CommandWithSoundDelegate handler, Arena arena)
        {
            _rwLock.EnterWriteLock();

            try
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
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        event DefaultCommandDelegate ICommandManager.DefaultCommandReceived
        {
            add
            {
                lock (_lockObj)
                {
                    if (_defaultCommandEvent != null)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(CommandManager),
                            $"A {nameof(ICommandManager.DefaultCommandReceived)} is being subscribed to when one is already subscribed. " +
                            $"Check that you're not loading more than 1 module that registers a default handler (e.g. only one billing module allowed).");
                    }

                    _defaultCommandEvent += value;
                }
            }

            remove
            {
                lock (_lockObj)
                {
                    _defaultCommandEvent -= value;
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

            CommandDelegate basicHandlers = null;
            CommandWithSoundDelegate soundHandlers = null;

            if (!skipLocal)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_cmdLookup.TryGetValue(cmd, out LinkedList<CommandData> list))
                    {
                        foreach (CommandData cd in list)
                        {
                            if (cd.Arena != null && cd.Arena != p.Arena)
                                continue;

                            if (cd is BasicCommandData basicData)
                                basicHandlers = (basicHandlers == null) ? basicData.Handler : basicHandlers += basicData.Handler;
                            else if (cd is SoundCommandData soundData)
                                soundHandlers = (soundHandlers == null) ? soundData.Handler : soundHandlers += soundData.Handler;
                        }
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            bool foundLocal = basicHandlers != null || soundHandlers != null;

            if (skipLocal || !foundLocal)
            {
                // send it to the biller
                _defaultCommandEvent?.Invoke(cmd, origLine, p, target);
            }
            else if (foundLocal)
            {
                if (Allowed(p, cmd, prefix, remoteArena))
                {
                    LogCommand(p, target, cmd, parameters);

                    basicHandlers?.Invoke(cmd, parameters, p, target);
                    soundHandlers?.Invoke(cmd, parameters, p, target, sound);
                }
#if CFG_LOG_ALL_COMMAND_DENIALS
                else
                {
                    _logManager.LogP(LogLevel.Drivel, nameof(CommandManager), p, "Permission denied for command '{0}'.", cmd);
                }
#endif
            }
        }

        string ICommandManager.GetHelpText(string commandName, Arena arena)
        {
            string ret = null;

            _rwLock.EnterReadLock();

            try
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
            finally
            {
                _rwLock.ExitReadLock();
            }

            return ret;
        }

        void ICommandManager.AddUnlogged(string commandName)
        {
            _rwLock.EnterWriteLock();

            try
            {
                _unloggedCommands.Add(commandName);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        void ICommandManager.RemoveUnlogged(string commandName)
        {
            _rwLock.EnterWriteLock();

            try
            {
                _unloggedCommands.Remove(commandName);
            }
            finally
            {
                _rwLock.ExitWriteLock();
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
            _rwLock.EnterReadLock();

            try
            {
                if (_unloggedCommands.Contains(cmd))
                    parameters = "...";
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

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
            _rwLock.EnterWriteLock();
            
            try
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
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player, 
            Description =
            "Displays all the commands that you (or the specified player) can use.\n" +
            "Commands in the arena section are specific to the current arena.\n" +
            "The symbols before each command specify how the command can be used:\n" +
            "A dot '.' means you use the command without sending it to a player, " +
            "it might apply to the entire zone, the current arena or to yourself.\n" +
            "A slash '/' means you can send the command in a private message to a player, " +
            "the effects will then apply to that player only.\n" +
            "A colon ':' means you can send the command in a private message to a player in a different arena")]
        private void Command_commands(string commandName, string parameters, Player p, ITarget target)
        {
            if (_chat == null)
                return;

            if (target.Type == TargetType.Player)
            {
                Player playerTarget = ((IPlayerTarget)target).Player;
                _chat.SendMessage(p, $"'{playerTarget.Name}' can use the following commands:");
                ListCommands(p.Arena, playerTarget, p, false, true);
            }
            else
            {
                _chat.SendMessage(p, "You can use the following commands:");
                ListCommands(p.Arena, p, p, false, true);
            }

            string helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(helpCommandName))
                helpCommandName = "man";

            _chat.SendMessage(p, $"Use ?{helpCommandName} <command name> to learn more about a command.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description =
            "Displays all commands, including those you don't have access to use.\n" +
            "Commands in the arena section are specific to the current arena.\n" +
            "The symbols before each command specify how the command can be used:\n" +
            "An exclamation mark '!' means you don't have access to use the command.\n" +
            "A dot '.' means you use the command without sending it to a player, " +
            "it might apply to the entire zone, the current arena or to yourself.\n" +
            "A slash '/' means you can send the command in a private message to a player, " +
            "the effects will then apply to that player only.\n" +
            "A colon ':' means you can send the command in a private message to a player in a different arena")]
        private void Command_allcommands(string commandName, string parameters, Player p, ITarget target)
        {
            if (_chat == null)
                return;

            _chat.SendMessage(p, "All commands:");
            ListCommands(p.Arena, p, p, false, false);

            string helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(helpCommandName))
                helpCommandName = "man";

            _chat.SendMessage(p, $"Use ?{helpCommandName} <command name> to learn more about a command.");
        }

        private void ListCommands(Arena arena, Player p, Player sendTo, bool excludeGlobal, bool excludeNoAccess)
        {
            if (_chat == null)
                return;

            _rwLock.EnterReadLock();

            try
            {
                var commands = 
                    from kvp in _cmdLookup
                    let command = kvp.Key
                    let canArena = Allowed(p, command, "cmd", null)
                    let canPriv = Allowed(p, command, "privcmd", null)
                    let canRemotePriv = Allowed(p, command, "rprivcmd", null)
                    where !excludeNoAccess || canArena || canPriv || canRemotePriv
                    from commandData in kvp.Value
                    where (!excludeGlobal && commandData.Arena == null) || commandData.Arena == arena
                    orderby command
                    select (command, isArenaSpecific: commandData.Arena != null, canArena, canPriv, canRemotePriv);

                StringBuilder sb = new(); // TODO: get from a pool

                if (!excludeGlobal)
                {
                    var globalCommands = commands.Where(c => !c.isArenaSpecific);
                    if (globalCommands.Any())
                    {
                        sb.Append("Zone:");
                        AppendCommands(sb, globalCommands);
                        _chat.SendWrappedText(sendTo, sb.ToString());
                    }
                }

                sb.Clear();

                var arenaCommands = commands.Where(c => c.isArenaSpecific);
                if (arenaCommands.Any())
                {
                    sb.Append("Arena:");
                    AppendCommands(sb, arenaCommands);
                    _chat.SendWrappedText(sendTo, sb.ToString());
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            static void AppendCommands(StringBuilder sb, IEnumerable<(string command, bool isArenaSpecific, bool canArena, bool canPriv, bool canRemotePriv)> commands)
            {
                foreach (var (command, isArenaSpecific, canArena, canPriv, canRemotePriv) in commands)
                {
                    sb.Append(' ');

                    if (!canArena && !canPriv && !canRemotePriv)
                    {
                        sb.Append('!');
                    }
                    else
                    {
                        if (canArena)
                            sb.Append('.');

                        if (canPriv)
                            sb.Append('/');

                        if (canRemotePriv)
                            sb.Append(':');
                    }

                    sb.Append(command);
                }
            }
        }
    }
}
