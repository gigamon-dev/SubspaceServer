using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Logging module that adds in-game log functionality for staff members.
    /// 
    /// <para>
    /// The ability notify staff of logs via chat messages.
    /// Log filtering is configured by normal log settings in the [log_sysop] configuration section.
    /// By default, it is configured to send <see cref="LogLevel.Malicious"/> and <see cref="LogLevel.Error"/> logs from all modules.
    /// Users with the <see cref="Constants.Capabilities.SeeSysopLogAll"/> capability will see all logs.
    /// Users with the <see cref="Constants.Capabilities.SeeSysopLogArena"/> capability will only see logs for the arena they are in.
    /// By default, sysops have the <see cref="Constants.Capabilities.SeeSysopLogAll"/> capability and smods have the <see cref="Constants.Capabilities.SeeSysopLogArena"/> capability.
    /// However, it is configurable through the normal capability/group settings.
    /// </para>
    /// 
    /// <para>
    /// Adds the ?lastlog command which provides the ability to view and filter logs from in-game.
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public class LogSysop : IModule
    {
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        private PlayerDataKey<PlayerData> _pdKey;

        private int MaxLast = 640; // TODO: make this a config setting?
        private static readonly int MaxLine = ChatPacket.MaxMessageChars;
        private int MaxAtOnce = 50; // TODO: make this a config setting?

        private Memory<char>[] _logData;
        private int _logPosition = 0;
        private readonly object _lockObj = new();

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            Memory<char> fullLogBuffer = new(new char[MaxLast * MaxLine]);
            _logData = new Memory<char>[MaxLast];
            for (int i = 0; i < _logData.Length; i++)
                _logData[i] = fullLogBuffer.Slice(i * MaxLine, MaxLine);

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            LogCallback.Register(broker, Callback_Log);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _commandManager.AddCommand("lastlog", Command_lastlog);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand("lastlog", Command_lastlog);

            LogCallback.Unregister(broker, Callback_Log);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region Callback handlers

        private void Callback_Log(in LogEntry logEntry)
        {
            if (_logManager.FilterLog(in logEntry, "log_sysop"))
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    _playerData.Lock();
                    try
                    {
                        foreach (Player p in _playerData.Players)
                        {
                            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                                return;

                            if (pd.SeeWhat == SeeWhat.All
                                || pd.SeeWhat == SeeWhat.Arena && logEntry.Arena == p.Arena)
                            {
                                set.Add(p);
                            }
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    _chat.SendAnyMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, logEntry.LogText);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }

            if (_logManager.FilterLog(in logEntry, "log_lastlog"))
            {
                lock (_lockObj)
                {
                    Memory<char> buffer = _logData[_logPosition];
                    Span<char> span = buffer.Span;
                    span.Clear();
                    logEntry.LogText.CopyTo(0, span, Math.Min(logEntry.LogText.Length, span.Length));

                    _logPosition = (_logPosition + 1) % _logData.Length;
                }
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Connect || action == PlayerAction.EnterArena)
            {
                if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                    return;

                if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeSysopLogAll))
                    pd.SeeWhat = SeeWhat.All;
                else if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeSysopLogArena))
                    pd.SeeWhat = SeeWhat.Arena;
                else
                    pd.SeeWhat = SeeWhat.None;
            }
        }

        #endregion

        #region Command handlers

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[<number of lines>] [<limiting text>]",
            Description =
            "Displays the last <number> lines in the server log (default: 10).\n" +
            "If limiting text is specified, only lines that contain that text will\n" +
            "be displayed. If a player is targeted, only lines mentioning that player\n" +
            "will be displayed.")]
        private void Command_lastlog(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player p, ITarget target)
        {
            target.TryGetPlayerTarget(out Player targetPlayer);

            Span<char> nameFilter = targetPlayer == null
                ? Span<char>.Empty
                : stackalloc char[targetPlayer.Name.Length + 2];

            if (!nameFilter.IsEmpty)
            {
                MemoryExtensions.TryWrite(nameFilter, $"[{targetPlayer.Name}]", out _);
            }

            ReadOnlySpan<char> args = parameters;
            ReadOnlySpan<char> token = args.GetToken(' ', out ReadOnlySpan<char> textFilter);

            if (int.TryParse(token, out int count))
            {
                count = Math.Clamp(count, 1, MaxAtOnce);
            }
            else
            {
                count = 10;
                textFilter = args;
            }

            textFilter = textFilter.Trim();

            Memory<char>[] lines = ArrayPool<Memory<char>>.Shared.Rent(MaxAtOnce);

            try
            {
                lock (_lockObj)
                {
                    // Move backwards and find lines that match the filters.
                    int left = count;
                    int c = (_logPosition - 1 + MaxLast) % MaxLast;
                    while (c != _logPosition && left > 0)
                    {
                        Memory<char> log = _logData[c].TrimEnd('\0');
                        if (log.Length == 0)
                            break;

                        Span<char> logText = log.Span;

                        if ((nameFilter.IsEmpty || MemoryExtensions.Contains(logText, nameFilter, StringComparison.OrdinalIgnoreCase))
                            && (textFilter.IsEmpty || MemoryExtensions.Contains(logText, textFilter, StringComparison.OrdinalIgnoreCase)))
                        {
                            lines[--left] = log;
                        }

                        c = (c - 1 + MaxLast) % MaxLast;
                    }

                    // Print the lines out as chat messages.
                    var set = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        set.Add(p);

                        while (left < count)
                        {
                            _chat.SendAnyMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, lines[left].Span);
                            lines[left++] = default;
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }
                }
            }
            finally
            {
                ArrayPool<Memory<char>>.Shared.Return(lines);
            }
        }

        #endregion

        #region Private helpers

        private enum SeeWhat
        {
            None,
            Arena,
            All,
        }

        private class PlayerData : IPooledExtraData
        {
            public SeeWhat SeeWhat;

            public void Reset()
            {
                SeeWhat = SeeWhat.None;
            }
        }

        #endregion
    }
}
