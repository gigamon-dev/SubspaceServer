using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for dispatching commands run by players in chat messages.
    /// </summary>
    [CoreModuleInfo]
    public class CommandManager : IModule, ICommandManager, IModuleLoaderAware
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<ICommandManager> _iCommandManagerToken;

        private IChat _chat;

        private readonly ObjectPool<List<CommandSummary>> _commandSummaryListPool = new DefaultObjectPool<List<CommandSummary>>(new CommandSummaryListPooledObjectPolicy());

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
        private readonly Trie<LinkedList<CommandData>> _cmdLookup = new(false);
        private readonly Trie _unloggedCommands = new(false);

        private readonly object _defaultCommandLock = new();
        private event DefaultCommandDelegate DefaultCommandEvent;

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            ILogManager logManager,
            ICapabilityManager capabilityManager,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

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

            lock (_defaultCommandLock)
            {
                DefaultCommandEvent = null;
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
            if (broker.UnregisterInterface(ref _iCommandManagerToken) != 0)
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

            lock (_defaultCommandLock)
            {
                DefaultCommandEvent = null;
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
                    _cmdLookup.TryAdd(commandName, ll);
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

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        sb.Append($"Targets: {helpAttr.Targets:F}\n");
                        sb.Append($"Args: {helpAttr.Args ?? "None"}\n");

                        if (!string.IsNullOrWhiteSpace(helpAttr.Description))
                            sb.Append(helpAttr.Description);

                        helpText = sb.ToString();
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }

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
                    _cmdLookup.Remove(commandName, out _);
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
                    _cmdLookup.Remove(commandName, out _);
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
                lock (_defaultCommandLock)
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
                lock (_defaultCommandLock)
                {
                    DefaultCommandEvent -= value;
                }
            }
        }

        void ICommandManager.Command(ReadOnlySpan<char> typedLine, Player player, ITarget target, ChatSound sound)
        {
            if (player == null)
                return;

            // almost all commands assume that p.Arena is not null
            if (player.Arena == null)
                return;

            if (target == null)
                return;

            // NOTE: The Chat module has already removed the starting ? or * from typedLine

            typedLine = typedLine.Trim();
            if (typedLine.IsWhiteSpace())
                return;

            bool skipLocal = false;

            // ?\<command> is a way to send a command straight to the the billing server
            if (typedLine[0] == '\\')
            {
                typedLine = typedLine[1..];
                if (typedLine.IsWhiteSpace())
                    return;

                skipLocal = true;
            }

            ReadOnlySpan<char> origLine = typedLine;

            // TODO: ASSS cuts command name at 30 characters? why the limit?
            // TODO: ASSS ends command name on ' ', '=', and '#'.  Then for parameters it skips ' ' and '=', but will leave a '#' at the start in the parameters string?
            // = makes sense for ?chat=first,second,third or ?password=newpassword etc...
            // where is # used?

            ReadOnlySpan<char> cmd = typedLine.GetToken(" =", out ReadOnlySpan<char> parameters);
            if (cmd.IsEmpty)
                return;

            parameters = parameters.TrimStart(" =");

            string prefix;
            Arena remoteArena = null;

            if (target.Type == TargetType.Arena || target.Type == TargetType.None)
            {
                prefix = "cmd";
            }
            else if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                if (targetPlayer.Arena == player.Arena)
                {
                    prefix = "privcmd";
                }
                else
                {
                    remoteArena = targetPlayer.Arena;
                    prefix = "rprivcmd";
                }
            }
            else
            {
                prefix = "privcmd";
            }

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
                            if (cd.Arena != null && cd.Arena != player.Arena)
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
                DefaultCommandEvent?.Invoke(cmd, origLine, player, target);
            }
            else if (foundLocal)
            {
                if (Allowed(player, cmd, prefix, remoteArena))
                {
                    LogCommand(player, target, cmd, parameters, sound);

                    try
                    {
                        basicHandlers?.Invoke(cmd, parameters, player, target);
                        soundHandlers?.Invoke(cmd, parameters, player, target, sound);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogP(LogLevel.Error, nameof(CommandManager), player, $"Handler for command '{cmd}' threw an exception. {ex}");
                    }
                }
#if CFG_LOG_ALL_COMMAND_DENIALS
                else
                {
                    _logManager.LogP(LogLevel.Drivel, nameof(CommandManager), player, $"Permission denied for command '{cmd}'.");
                }
#endif
            }
        }

        string ICommandManager.GetHelpText(ReadOnlySpan<char> commandName, Arena arena)
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

        void ICommandManager.AddUnlogged(ReadOnlySpan<char> commandName)
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

        void ICommandManager.RemoveUnlogged(ReadOnlySpan<char> commandName)
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

        private bool Allowed(Player player, ReadOnlySpan<char> cmd, string prefix, Arena remoteArena)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (cmd.IsEmpty)
                throw new ArgumentException("Cannot be empty.", nameof(cmd));

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

            Span<char> capability = stackalloc char[prefix.Length + 1 + cmd.Length];
            bool success = capability.TryWrite($"{prefix}_{cmd}", out int charsWritten);
            Debug.Assert(success && charsWritten == capability.Length);

            if (remoteArena != null)
                return _capabilityManager.HasCapability(player, remoteArena, capability);
            else
                return _capabilityManager.HasCapability(player, capability);
        }

        private void LogCommand(Player player, ITarget target, ReadOnlySpan<char> cmd, ReadOnlySpan<char> parameters, ChatSound sound)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (cmd.IsEmpty)
                throw new ArgumentException("Cannot be empty.", nameof(cmd));

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

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append("Command ");

                if (target.TryGetArenaTarget(out _))
                    sb.Append("(arena)");
                else if (target.TryGetTeamTarget(out _, out int freq))
                    sb.Append($"(freq {freq})");
                else if (target.TryGetPlayerTarget(out Player targetPlayer))
                    sb.Append($"to [{targetPlayer.Name}]");
                else
                    sb.Append("(other)");

                sb.Append($": {cmd}");

                if (!parameters.IsWhiteSpace())
                    sb.Append($" {parameters}");

                _logManager.LogP(LogLevel.Info, nameof(CommandManager), player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            CommandExecutedCallback.Fire(player.Arena ?? _broker, player, target, cmd, parameters, sound);
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
        private void Command_commands(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_chat == null)
                return;

            if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, $"'{targetPlayer.Name}' can use the following commands:");
                ListCommands(player.Arena, targetPlayer, player, false, true);
            }
            else
            {
                _chat.SendMessage(player, "You can use the following commands:");
                ListCommands(player.Arena, player, player, false, true);
            }

            string helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(helpCommandName))
                helpCommandName = "man";

            _chat.SendMessage(player, $"Use ?{helpCommandName} <command name> to learn more about a command.");
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
        private void Command_allcommands(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_chat == null)
                return;

            _chat.SendMessage(player, "All commands:");
            ListCommands(player.Arena, player, player, false, false);

            string helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(helpCommandName))
                helpCommandName = "man";

            _chat.SendMessage(player, $"Use ?{helpCommandName} <command name> to learn more about a command.");
        }

        private void ListCommands(Arena arena, Player player, Player sendTo, bool excludeGlobal, bool excludeNoAccess)
        {
            if (_chat == null)
                return;

            List<CommandSummary> globalCommands = _commandSummaryListPool.Get();
            List<CommandSummary> arenaCommands = _commandSummaryListPool.Get();

            _rwLock.EnterReadLock();

            try
            {
                foreach (var (commandName, commandDataList) in _cmdLookup)
                {
                    var commandNameSpan = commandName.Span;
                    bool canArena = Allowed(player, commandNameSpan, "cmd", null);
                    bool canPriv = Allowed(player, commandNameSpan, "privcmd", null);
                    bool canRemotePriv = Allowed(player, commandNameSpan, "rprivcmd", null);

                    if (excludeNoAccess && !canArena && !canPriv && !canRemotePriv)
                        continue;

                    foreach (var commandData in commandDataList)
                    {
                        List<CommandSummary> list = null;
                        if (commandData.Arena is null)
                        {
                            if (!excludeGlobal)
                            {
                                list = globalCommands;
                            }
                        }
                        else if (commandData.Arena == arena)
                        {
                            list = arenaCommands;
                        }

                        if (list is not null)
                        {
                            list?.Add(
                                new CommandSummary()
                                {
                                    Command = new MutableStringBuffer(commandNameSpan), // can't hold onto kvp.Key, create a copy of the string
                                    CanArena = canArena,
                                    CanPriv = canPriv,
                                    CanRemotePriv = canRemotePriv,
                                });
                        }
                    }
                }

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    if (globalCommands.Count > 0)
                    {
                        globalCommands.Sort(CommandSummaryComparer.OrdinalIgnoreCase);

                        sb.Append("Zone:");
                        AppendCommands(sb, globalCommands);
                        _chat.SendWrappedText(sendTo, sb);
                    }

                    sb.Clear();

                    if (arenaCommands.Count > 0)
                    {
                        arenaCommands.Sort(CommandSummaryComparer.OrdinalIgnoreCase);

                        sb.Append("Arena:");
                        AppendCommands(sb, arenaCommands);
                        _chat.SendWrappedText(sendTo, sb);
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
            finally
            {
                _rwLock.ExitReadLock();

                foreach (var summary in globalCommands)
                    summary.Command.Dispose();

                foreach (var summary in arenaCommands)
                    summary.Command.Dispose();

                _commandSummaryListPool.Return(globalCommands);
                _commandSummaryListPool.Return(arenaCommands);
            }

            static void AppendCommands(StringBuilder sb, List<CommandSummary> commands)
            {
                foreach (CommandSummary commandSummary in commands)
                {
                    sb.Append(' ');

                    if (!commandSummary.CanArena && !commandSummary.CanPriv && !commandSummary.CanRemotePriv)
                    {
                        sb.Append('!');
                    }
                    else
                    {
                        if (commandSummary.CanArena)
                            sb.Append('.');

                        if (commandSummary.CanPriv)
                            sb.Append('/');

                        if (commandSummary.CanRemotePriv)
                            sb.Append(':');
                    }

                    sb.Append(commandSummary.Command.Value);
                }
            }
        }

        #region Helper types

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

        private struct CommandSummary
        {
            public MutableStringBuffer Command { get; set; }
            public bool CanArena { get; set; }
            public bool CanPriv { get; set; }
            public bool CanRemotePriv { get; set; }
        }

        private class CommandSummaryListPooledObjectPolicy : PooledObjectPolicy<List<CommandSummary>>
        {
            public override List<CommandSummary> Create()
            {
                return new List<CommandSummary>();
            }

            public override bool Return(List<CommandSummary> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class CommandSummaryComparer : IComparer<CommandSummary>
        {
            public static readonly CommandSummaryComparer Ordinal = new(StringComparison.Ordinal);
            public static readonly CommandSummaryComparer OrdinalIgnoreCase = new(StringComparison.OrdinalIgnoreCase);

            private readonly StringComparison _stringComparison;

            private CommandSummaryComparer(StringComparison stringComparison)
            {
                _stringComparison = stringComparison;
            }

            public int Compare(CommandSummary x, CommandSummary y)
            {
                return MemoryExtensions.CompareTo(x.Command.Value, y.Command.Value, _stringComparison);
            }
        }

        private struct MutableStringBuffer : IDisposable
        {
            public char[] Array { get; private set; }
            public int Length { get; private set; }

            public ReadOnlySpan<char> Value => Array is null ? ReadOnlySpan<char>.Empty : new ReadOnlySpan<char>(Array, 0, Length);

            public MutableStringBuffer(ReadOnlySpan<char> value)
            {
                Array = null;
                Length = 0;

                Set(value);
            }

            public void Set(ReadOnlySpan<char> value)
            {
                if (Array is not null 
                    && (value.IsEmpty || Array.Length < value.Length))
                {
                    ArrayPool<char>.Shared.Return(Array);
                    Array = null;
                }

                if (value.IsEmpty)
                {
                    Length = 0;
                    return;
                }

                Array ??= ArrayPool<char>.Shared.Rent(value.Length);
                value.CopyTo(Array);
                Length = value.Length;
            }

            public void Dispose()
            {
                Set(ReadOnlySpan<char>.Empty);
            }
        }

        #endregion
    }
}
