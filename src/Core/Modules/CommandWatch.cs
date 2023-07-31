using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that watches for a configured set of commands to be executed and notifies staff members when they are.
    /// </summary>
    /// <remarks>
    /// This is the equivalent of the log_staff module in ASSS.
    /// </remarks>
    public class CommandWatch : IModule
    {
        private IChat _chat;
        private IConfigManager _configManager;
        private IObjectPoolManager _objectPoolManager;

        private readonly Trie _watchedCommands = new(false);
        private readonly object _lockObj = new();

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            Initialize();

            CommandExecutedCallback.Register(broker, Callback_CommandExecuted);
            GlobalConfigChangedCallback.Register(broker, Initialize);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            CommandExecutedCallback.Unregister(broker, Callback_CommandExecuted);
            GlobalConfigChangedCallback.Unregister(broker, Initialize);
            return true;
        }

        #endregion

        private void Callback_CommandExecuted(Player player, ITarget target, ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, ChatSound sound)
        {
            lock (_lockObj)
            {
                if (!_watchedCommands.Contains(command))
                    return;
            }

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                if (player.Arena is not null)
                    sb.Append($"{{{player.Arena.Name}}} ");

                sb.Append($"{player.Name} command ");

                if (target.TryGetArenaTarget(out _))
                    sb.Append("(arena)");
                else if (target.TryGetTeamTarget(out _, out short freq))
                    sb.Append($"(freq {freq})");
                else if (target.TryGetPlayerTarget(out Player targetPlayer))
                    sb.Append($"to [{targetPlayer.Name}]");
                else
                    sb.Append("(other)");

                sb.Append($": {command}");

                if (!parameters.IsWhiteSpace())
                    sb.Append($" {parameters}");

                if (sound != ChatSound.None)
                    sb.Append($" %{(byte)sound}");

                _chat.SendModMessage(sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [ConfigHelp("log_staff", "commands", ConfigScope.Global, typeof(string), DefaultValue = "warn kick setcm",
            Description = "A list of commands that trigger messages to all logged-in staff.")]
        private void Initialize()
        {
            lock (_lockObj)
            {
                _watchedCommands.Clear();

                // Using the same setting name as ASSS for compatibility.
                ReadOnlySpan<char> commands = _configManager.GetStr(_configManager.Global, "log_staff", "commands") ?? "warn kick setcm";
                ReadOnlySpan<char> command;
                while (!(command = commands.GetToken(" ,:;", out commands)).IsEmpty)
                {
                    _watchedCommands.Add(command);
                }
            }
        }
    }
}
