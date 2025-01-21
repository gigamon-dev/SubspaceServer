using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
	/// <summary>
	/// Module that watches for a configured set of commands to be executed and notifies staff members when they are.
	/// </summary>
	/// <remarks>
	/// This is the equivalent of the log_staff module in ASSS.
	/// </remarks>
	public sealed class CommandWatch : IModule
    {
        private readonly IChat _chat;
		private readonly IConfigManager _configManager;
		private readonly IObjectPoolManager _objectPoolManager;

		private readonly HashSet<string> _watchedCommands = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _watchedCommandsLookup;
        private readonly Lock _lock = new();

		public CommandWatch(
            IChat chat,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager)
		{
			_chat = chat ?? throw new ArgumentNullException(nameof(chat));
			_configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
			_objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _watchedCommandsLookup = _watchedCommands.GetAlternateLookup<ReadOnlySpan<char>>();
		}

		#region Module members

		bool IModule.Load(IComponentBroker broker)
        {
            Initialize();

            CommandExecutedCallback.Register(broker, Callback_CommandExecuted);
            GlobalConfigChangedCallback.Register(broker, Initialize);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            CommandExecutedCallback.Unregister(broker, Callback_CommandExecuted);
            GlobalConfigChangedCallback.Unregister(broker, Initialize);
            return true;
        }

        #endregion

        private void Callback_CommandExecuted(Player player, ITarget target, ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, ChatSound sound)
        {
            lock (_lock)
            {
                if (!_watchedCommandsLookup.Contains(command))
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
                else if (target.TryGetPlayerTarget(out Player? targetPlayer))
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

        [ConfigHelp("log_staff", "commands", ConfigScope.Global, Default = "warn kick setcm",
            Description = "A list of commands that trigger messages to all logged-in staff.")]
        private void Initialize()
        {
            lock (_lock)
            {
                _watchedCommands.Clear();

                // Using the same setting name as ASSS for compatibility.
                ReadOnlySpan<char> commands = _configManager.GetStr(_configManager.Global, "log_staff", "commands") ?? ConfigHelp.Constants.Global.log_staff.commands.Default;
                ReadOnlySpan<char> command;
                while (!(command = commands.GetToken(" ,:;", out commands)).IsEmpty)
                {
                    _watchedCommandsLookup.Add(command);
                }
            }
        }
    }
}
