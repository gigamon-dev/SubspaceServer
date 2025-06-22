﻿using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to send chat messages to players 
    /// that enter an arena (Misc:GreetMessage setting) and/or 
    /// periodically (Misc:PeriodicMessage0 through Misc:PeriodicMessage9 settings).
    /// </summary>
    [CoreModuleInfo]
    public sealed class Messages : IModule
    {
        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly IMainloopTimer _mainloopTimer;

        private const int MaxPeriodicMessages = 10;
        private static readonly string[] PeriodicMessageKeys = new string[10];

        static Messages()
        {
            for (int i = 0; i < MaxPeriodicMessages; i++)
                PeriodicMessageKeys[i] = $"PeriodicMessage{i}";
        }

        private ArenaDataKey<ArenaData> _adKey;

        public Messages(
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IMainloopTimer mainloopTimer)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _mainloopTimer.ClearTimer<Arena>(MainLoopTimer_ProcessPeriodicMessage, null);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region Callbacks

        [ConfigHelp("Misc", "GreetMessage", ConfigScope.Arena,
            Description = "The message to send to each player on entering the arena.")]
        [ConfigHelp("Misc", "PeriodicMessage0", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage1", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage2", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage3", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage4", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage5", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage6", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage7", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage8", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage9", ConfigScope.Arena,
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Destroy || action == ArenaAction.ConfChanged)
            {
                _mainloopTimer.ClearTimer<Arena>(MainLoopTimer_ProcessPeriodicMessage, arena);
            }

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.GreetMessage = _configManager.GetStr(arena.Cfg!, "Misc", "GreetMessage");

                ad.PeriodicMessageList.Clear();
                Span<Range> ranges = stackalloc Range[3];
                for (int i = 0; i < PeriodicMessageKeys.Length; i++)
                {
                    ReadOnlySpan<char> setting = _configManager.GetStr(arena.Cfg!, "Misc", PeriodicMessageKeys[i]);
                    if (setting.IsEmpty)
                        continue;

                    if (setting.Split(ranges, ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) != 3)
                        continue;

                    if (!int.TryParse(setting[ranges[0]], out int interval) || interval <= 0)
                        continue;

                    if (!int.TryParse(setting[ranges[1]], out int initialDelay))
                        continue;

                    ad.PeriodicMessageList.Add(new PeriodicMessage(setting[ranges[2]].ToString(), initialDelay, interval));
                }

                if (ad.PeriodicMessageList.Count > 0)
                    _mainloopTimer.SetTimer(MainLoopTimer_ProcessPeriodicMessage, 60000, 60000, arena, arena);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!arena!.TryGetExtraData(_adKey, out ArenaData? ad))
                    return;

                if (!string.IsNullOrWhiteSpace(ad.GreetMessage))
                {
                    _chat.SendMessage(player, ad.GreetMessage);
                }
            }
        }

        #endregion

        private bool MainLoopTimer_ProcessPeriodicMessage(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ad.MinuteCount++;

            foreach (PeriodicMessage periodicMessage in ad.PeriodicMessageList)
            {
                int diff = ad.MinuteCount - periodicMessage.InitialDelay;
                if (diff >= 0 && (diff % periodicMessage.Interval) == 0)
                {
                    _chat.SendArenaMessage(arena, periodicMessage.Message);
                }
            }

            return true;
        }

        #region Helper types

        private class PeriodicMessage
        {
            public PeriodicMessage(string message, int initialDelay, int interval)
            {
                Message = message;
                InitialDelay = initialDelay;
                Interval = interval;
            }

            public string Message { get; }
            public int InitialDelay { get; }
            public int Interval { get; }
        }

        private class ArenaData : IResettable
        {
            public string? GreetMessage;
            public readonly List<PeriodicMessage> PeriodicMessageList = new(10);
            public int MinuteCount;

            public bool TryReset()
            {
                GreetMessage = null;
                PeriodicMessageList.Clear();
                MinuteCount = 0;
                return true;
            }
        }

        #endregion
    }
}
