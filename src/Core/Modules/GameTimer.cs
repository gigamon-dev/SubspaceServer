﻿using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides a timer for each arena.
    /// The timer can be linked to a "Timed Game" such as a game of speed zone.
    /// </summary>
    [CoreModuleInfo]
    public sealed class GameTimer(
        IArenaManager arenaManager,
        IChat chat,
        ICommandManager commandManager,
        IConfigManager configManager,
        ILogManager logManager,
        IMainloopTimer mainloopTimer,
        IObjectPoolManager objectPoolManager) : IModule, IGameTimer
    {
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private InterfaceRegistrationToken<IGameTimer>? _gameTimerRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _mainloopTimer.SetTimer(MainloopTimer_ProcessGameTimers, 1000, 1000, null);

            _commandManager.AddCommand("timer", Command_timer);
            _commandManager.AddCommand("time", Command_time);
            _commandManager.AddCommand("timereset", Command_timereset);
            _commandManager.AddCommand("pausetimer", Command_pausetimer);

            _gameTimerRegistrationToken = broker.RegisterInterface<IGameTimer>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _gameTimerRegistrationToken);

            _commandManager.RemoveCommand("timer", Command_timer);
            _commandManager.RemoveCommand("time", Command_time);
            _commandManager.RemoveCommand("timereset", Command_timereset);
            _commandManager.RemoveCommand("pausetimer", Command_pausetimer);

            _mainloopTimer.ClearTimer(MainloopTimer_ProcessGameTimers, null);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region IGameTimer

        bool IGameTimer.SetTimer(Arena arena, TimeSpan duration)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.GameLength == TimeSpan.Zero)
            {
                if (duration > TimeSpan.Zero)
                {
                    bool started = ad.Start(DateTime.UtcNow, duration);
                    if (started)
                    {
                        GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.InterfaceCall, ad.GameLength != TimeSpan.Zero);
                    }

                    return started;
                }
                else
                {
                    ad.Stop();
                    GameTimerChangedCallback.Fire(arena, arena, TimerChange.Stopped, TimerChangeReason.InterfaceCall, ad.GameLength != TimeSpan.Zero);
                    return true;
                }
            }
            else
            {
                return false;
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<duration>",
            Description = "Sets the arena timer. Not for arenas using a Misc:TimedGame.")]
        private void Command_timer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.GameLength != TimeSpan.Zero)
            {
                _chat.SendMessage(player, "Timer is fixed to the Misc:TimedGame setting.");
                return;
            }

            if (!TimeSpan.TryParse(parameters, out TimeSpan duration)) // TODO: change to accept the format the ASSS uses (<minutes>[:<seconds>])
            {
                _chat.SendMessage(player, $"Invalid duration specified.");
                return;
            }

            if (duration > TimeSpan.Zero)
            {
                if (ad.Start(DateTime.UtcNow, duration))
                {
                    GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.PlayerCommand, ad.GameLength != TimeSpan.Zero);
                    Command_time(commandName, parameters, player, target);
                }
            }
            else
            {
                ad.Stop();
                GameTimerChangedCallback.Fire(arena, arena, TimerChange.Stopped, TimerChangeReason.PlayerCommand, ad.GameLength != TimeSpan.Zero);
                Command_time(commandName, parameters, player, target);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Returns the amount of time left in the current game.")]
        private void Command_time(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.IsEnabled)
            {
                TimeSpan remaining = ad.EndingTimestamp!.Value - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    sb.Append("Time left: ");
                    AppendTimerDuration(sb, remaining);
                    sb.Append('.');

                    _chat.SendMessage(player, sb);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
            else if (ad.PausedRemaining != null)
            {
                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    sb.Append("Timer paused at: ");
                    AppendTimerDuration(sb, ad.PausedRemaining.Value);
                    sb.Append('.');

                    _chat.SendMessage(player, sb);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
            else
            {
                _chat.SendMessage(player, $"Time left: 0 seconds.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Resets a timed game for arenas using the Misc:TimedGame setting.")]
        private void Command_timereset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.GameLength == TimeSpan.Zero)
                return;

            if (ad.Start(DateTime.UtcNow))
            {
                GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.PlayerCommand, ad.GameLength != TimeSpan.Zero);
                Command_time(commandName, parameters, player, target);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = """
                Toggles the time between paused and unpaused.
                The timer must have been created with ?timer.
                """)]
        private void Command_pausetimer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.GameLength != TimeSpan.Zero)
                return;

            if (ad.IsEnabled)
            {
                if (ad.Pause())
                {
                    GameTimerChangedCallback.Fire(arena, arena, TimerChange.Paused, TimerChangeReason.PlayerCommand, ad.GameLength != TimeSpan.Zero);
                    Command_time(commandName, parameters, player, target);
                }
            }
            else
            {
                if (ad.Unpause(out TimeSpan remaining))
                {
                    GameTimerChangedCallback.Fire(arena, arena, TimerChange.Unpaused, TimerChangeReason.PlayerCommand, ad.GameLength != TimeSpan.Zero);

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        sb.Append("Timer resumed at: ");
                        AppendTimerDuration(sb, remaining);
                        sb.Append('.');

                        _chat.SendMessage(player, sb);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
            }
        }

        #endregion

        [ConfigHelp("Misc", "TimerWarnings", ConfigScope.Arena,
            Description = """
                Comma delimited list defining when notifications should be sent to the arena.
                Values are in seconds, indicating the remaining time. E.g. a value of "10,30" means send
                notifications when when 10 seconds remain and when 30 seconds remain.
                """)]
        [ConfigHelp<int>("Misc", "TimedGame", ConfigScope.Arena, Default = 0,
            Description = "How long the game timer lasts (in ticks). Zero to disable.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.WarningAt.Clear();
                ReadOnlySpan<char> warnStr = _configManager.GetStr(arena.Cfg!, "Misc", "TimerWarnings");
                foreach (Range range in warnStr.SplitAny(" ,"))
                {
                    ReadOnlySpan<char> token = warnStr[range].Trim();
                    if (token.IsEmpty)
                        continue;
                    
                    if (int.TryParse(token, out int seconds) && seconds > 0)
                    {
                        ad.WarningAt.Add(TimeSpan.FromSeconds(seconds));
                    }
                }
                ad.WarningAt.Sort();

                TimeSpan oldGameLength = ad.GameLength;
                ad.GameLength = TimeSpan.FromMilliseconds(_configManager.GetInt(arena.Cfg!, "Misc", "TimedGame", ConfigHelp.Constants.Arena.Misc.TimedGame.Default) * 10);

                if (action == ArenaAction.Create && ad.GameLength > TimeSpan.Zero)
                {
                    if (ad.Start(DateTime.UtcNow))
                    {
                        GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.ArenaAction, ad.GameLength != TimeSpan.Zero);
                    }
                }
                else if (action == ArenaAction.ConfChanged && oldGameLength != ad.GameLength)
                {
                    if (ad.GameLength > TimeSpan.Zero && ad.IsEnabled)
                    {
                        if (ad.Start(DateTime.UtcNow))
                        {
                            GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.ArenaAction, ad.GameLength != TimeSpan.Zero);
                        }
                    }
                    else
                    {
                        ad.Stop();
                        GameTimerChangedCallback.Fire(arena, arena, TimerChange.Stopped, TimerChangeReason.ArenaAction, true); // it was a timed game, but no longer is (that's why it's being stopped)
                    }
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                ad.IsEnabled = false;
            }
        }

        private bool MainloopTimer_ProcessGameTimers()
        {
            DateTime now = DateTime.UtcNow;

            _arenaManager.Lock();

            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                        continue;

                    if (!ad.IsEnabled)
                        continue;

                    // Send warning notifications.
                    TimeSpan remaining = ad.EndingTimestamp!.Value - now;
                    while (ad.NextWarningIndex >= 0)
                    {
                        TimeSpan warningTimeSpan = ad.WarningAt[ad.NextWarningIndex];
                        if (remaining > warningTimeSpan)
                            break;

                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            sb.Append("NOTICE: ");
                            AppendTimerDuration(sb, warningTimeSpan);
                            sb.Append(" remaining.");

                            _chat.SendArenaMessage(arena, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }

                        ad.NextWarningIndex--;
                    }

                    // Check if the timer ended.
                    if (now >= ad.EndingTimestamp.Value)
                    {
                        _logManager.LogA(LogLevel.Drivel, nameof(GameTimer), arena, "Timer expired.");

                        GameTimerEndedCallback.Fire(arena, arena);

                        if (ad.GameLength > TimeSpan.Zero)
                        {
                            if (ad.Start(now))
                            {
                                GameTimerChangedCallback.Fire(arena, arena, TimerChange.Started, TimerChangeReason.Completion, ad.GameLength != TimeSpan.Zero);
                            }
                        }
                        else
                        {
                            ad.Stop();
                            GameTimerChangedCallback.Fire(arena, arena, TimerChange.Stopped, TimerChangeReason.Completion, ad.GameLength != TimeSpan.Zero);
                        }

                        _chat.SendArenaMessage(arena, ChatSound.Hallellula, $"NOTICE: Game over");
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            return true;
        }

        private static void AppendTimerDuration(StringBuilder sb, TimeSpan duration)
        {
            if (sb == null)
                return;

            int totalMinutes = (int)duration.TotalMinutes;
            if (totalMinutes >= 1)
            {
                sb.Append($"{totalMinutes} minute");
                if (totalMinutes != 1)
                    sb.Append('s');

                sb.Append(" and ");
            }

            sb.Append($"{duration.Seconds} second");
            if (duration.Seconds != 1)
                sb.Append('s');
        }

        #region Helper types

        public class ArenaData : IResettable
        {
            /// <summary>
            /// Duration for timed games (Misc:TimedGame setting). Otherwise, <see cref="TimeSpan.Zero"/>.
            /// </summary>
            public TimeSpan GameLength;

            /// <summary>
            /// Whether the timer is running.
            /// </summary>
            public bool IsEnabled;

            /// <summary>
            /// Timestamp a running timer will end. <see langword="null"/> when the timer is not running.
            /// </summary>
            public DateTime? EndingTimestamp = null;

            /// <summary>
            /// Amount of time left on the timer when it is paused. <see langword="null"/> when not paused.
            /// </summary>
            public TimeSpan? PausedRemaining = null;

            /// <summary>
            /// Durations that define when warning notifications are sent, in increasing order.
            /// A value of 10 seconds, means a notification will be sent to the arena when there are 10 seconds left.
            /// </summary>
            public readonly List<TimeSpan> WarningAt = [];

            /// <summary>
            /// Index of the next notification. -1 when there is none.
            /// </summary>
            public int NextWarningIndex = -1;

            public bool Start(DateTime asOf)
            {
                return Start(asOf, GameLength);
            }

            public bool Start(DateTime asOf, TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                {
                    IsEnabled = false;
                    return false;
                }

                EndingTimestamp = asOf + duration;
                PausedRemaining = null;
                SetNextWarning(duration);
                IsEnabled = true;

                return true;
            }

            private void SetNextWarning(TimeSpan duration)
            {
                if (WarningAt.Count > 0)
                {
                    for (int i = WarningAt.Count - 1; i >= 0; i--)
                    {
                        if (WarningAt[i] <= duration)
                        {
                            NextWarningIndex = i;
                            return;
                        }
                    }

                    NextWarningIndex = -1;
                }
            }

            public void Stop()
            {
                IsEnabled = false;
                EndingTimestamp = null;
                PausedRemaining = null;
            }

            public bool Pause()
            {
                if (IsEnabled)
                {
                    TimeSpan remaining = EndingTimestamp!.Value - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        PausedRemaining = remaining;
                        IsEnabled = false;
                        EndingTimestamp = null;
                        return true;
                    }
                }

                return false;
            }

            public bool Unpause(out TimeSpan remaining)
            {
                if (!IsEnabled && PausedRemaining != null)
                {
                    remaining = PausedRemaining.Value;

                    if (Start(DateTime.UtcNow, remaining))
                    {
                        return true;
                    }
                    else
                    {
                        remaining = default;
                        return false;
                    }
                }

                remaining = default;
                return false;
            }

            public bool TryReset()
            {
                GameLength = TimeSpan.Zero;
                IsEnabled = false;
                EndingTimestamp = null;
                PausedRemaining = null;
                WarningAt.Clear();
                NextWarningIndex = -1;
                return true;
            }
        }

        #endregion
    }
}
