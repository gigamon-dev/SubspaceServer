using Microsoft.Extensions.ObjectPool;
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
    public class Messages : IModule
    {
        private IArenaManager _arenaManager;
        private IChat _chat;
        private IConfigManager _configManager;
        private IMainloopTimer _mainloopTimer;

        private const int MaxPeriodicMessages = 10;
        private static readonly string[] PeriodicMessageKeys = new string[10];

        static Messages()
        {
            for (int i = 0; i < MaxPeriodicMessages; i++)
                PeriodicMessageKeys[i] = $"PeriodicMessage{i}";
        }

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IMainloopTimer mainloopTimer)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));

            _adKey = _arenaManager.AllocateArenaData(new ArenaDataPooledObjectPolicy());

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _mainloopTimer.ClearTimer<Arena>(MainLoopTimer_ProcessPeriodicMessage, null);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region Callbacks

        [ConfigHelp("Misc", "GreetMessage", ConfigScope.Arena, typeof(string), 
            Description = "The message to send to each player on entering the arena.")]
        [ConfigHelp("Misc", "PeriodicMessage0", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage1", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage2", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage3", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage4", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage5", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage6", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage7", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage8", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        [ConfigHelp("Misc", "PeriodicMessage9", ConfigScope.Arena, typeof(string),
            Description = "10 20 periodic message. 10 is the interval and 20 is the initial delay (in minutes).")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Destroy || action == ArenaAction.ConfChanged)
            {
                _mainloopTimer.ClearTimer<Arena>(MainLoopTimer_ProcessPeriodicMessage, arena);
            }

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.GreetMessage = _configManager.GetStr(arena.Cfg, "Misc", "GreetMessage");

                ad.PeriodicMessageList.Clear();
                for (int i = 0; i < PeriodicMessageKeys.Length; i++)
                {
                    string setting = _configManager.GetStr(arena.Cfg, "Misc", PeriodicMessageKeys[i]);
                    if (setting == null)
                        continue;

                    ReadOnlySpan<char> remaining = setting;
                    ReadOnlySpan<char> token;
                    if ((token = remaining.GetToken(' ', out remaining)).IsEmpty)
                        continue;

                    if (!int.TryParse(token, out int interval) || interval <= 0)
                        continue;

                    if ((token = remaining.GetToken(' ', out remaining)).IsEmpty)
                        continue;

                    if (!int.TryParse(token, out int initialDelay))
                        continue;

                    if (remaining.IsWhiteSpace())
                        continue;

                    ad.PeriodicMessageList.Add(new PeriodicMessage(remaining.Trim().ToString(), initialDelay, interval));
                }

                if (ad.PeriodicMessageList.Count > 0)
                    _mainloopTimer.SetTimer(MainLoopTimer_ProcessPeriodicMessage, 60000, 60000, arena, arena);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
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
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
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

        private class ArenaData
        {
            public string GreetMessage;
            public List<PeriodicMessage> PeriodicMessageList = new(10);
            public int MinuteCount;
        }

        private class ArenaDataPooledObjectPolicy : PooledObjectPolicy<ArenaData>
        {
            public override ArenaData Create()
            {
                return new ArenaData();
            }

            public override bool Return(ArenaData ad)
            {
                if (ad == null)
                    return false;

                ad.GreetMessage = null;
                ad.PeriodicMessageList.Clear();
                ad.MinuteCount = 0;

                return true;
            }
        }

        #endregion
    }
}
