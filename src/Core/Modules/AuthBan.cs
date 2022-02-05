using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for temporarily kicking players off.
    /// This module addes the ?kick command and other associated commands to manage bans.
    /// </summary>
    public class AuthBan : IModule, IAuth
    {
        private IAuth _oldAuth;
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken _iAuthToken;

        private readonly Dictionary<uint, BanRecord> _banDictionary = new();
        private readonly object _lockObj = new();

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            IAuth auth,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _oldAuth = auth ?? throw new ArgumentNullException(nameof(auth));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException( nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _commandManager.AddCommand("kick", Command_kick);
            _commandManager.AddCommand("listkick", Command_listkick);
            _commandManager.AddCommand("listmidbans", Command_listkick);
            _commandManager.AddCommand("delkick", Command_delkick);
            _commandManager.AddCommand("liftkick", Command_delkick);
            _commandManager.AddCommand("delmidban", Command_delkick);

            _iAuthToken = broker.RegisterInterface<IAuth>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IAuth>(ref _iAuthToken);

            _commandManager.RemoveCommand("kick", Command_kick);
            _commandManager.RemoveCommand("listkick", Command_listkick);
            _commandManager.RemoveCommand("listmidbans", Command_listkick);
            _commandManager.RemoveCommand("delkick", Command_delkick);
            _commandManager.RemoveCommand("liftkick", Command_delkick);
            _commandManager.RemoveCommand("delmidban", Command_delkick);

            return true;
        }

        #endregion

        void IAuth.Authenticate(Player p, in LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            AuthData authData = null;

            lock (_lockObj)
            {
                if (p.IsStandard // only standard clients have a MacId
                    && _banDictionary.TryGetValue(lp.MacId, out BanRecord ban))
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < ban.Expire)
                    {
                        authData = new()
                        {
                            Code = AuthCode.CustomText,
                        };

                        authData.CustomText = string.IsNullOrWhiteSpace(ban.Reason)
                            ? $"You have been temporarily kicked. You may log in again in {ban.Expire - now}."
                            : $"You have been temporarily kicked for {ban.Reason}. You may log in again in {ban.Expire - now}.";
                        ban.Count++;

                        _logManager.LogM(LogLevel.Info, nameof(AuthBan), $"Player [{lp.Name}] tried to login (try {ban.Count}), banned for {ban.Expire - now} longer.");
                    }
                    else
                    {
                        _banDictionary.Remove(lp.MacId);
                    }
                }
            }

            if (authData != null)
            {
                done(p, authData);
            }
            else
            {
                _oldAuth.Authenticate(p, in lp, lplen, done);
            }
        }

        #region Command handlers

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "[-s seconds | -t seconds | -m minutes | seconds] [reason]",
            Description = 
            "Kicks the player off of the server, with an optional timeout. (-s number, -t number, or number for seconds, -m number for minutes.)\n" +
            "For kicks with a timeout, you may provide a message to be displayed to the user.\n" +
            "Messages appear to users on timeout as \"You have been temporarily kicked for <reason>.\"")]
        private void Command_kick(string commandName, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = target?.Type == TargetType.Player && target is IPlayerTarget pt ? pt.Player : null;
            if (targetPlayer == null)
            {
                _chat.SendMessage(p, "This comand  only operates when targeting a specific player.");
                return;
            }

            if (targetPlayer == p)
                return;

            if (!_capabilityManager.HigherThan(p, targetPlayer))
            {
                _chat.SendMessage(p, $"You don't have permission to use ?kick on {targetPlayer.Name}.");
                _chat.SendMessage(targetPlayer, $"{p.Name} tried to use ?kick on you.");
                return;
            }

            if (targetPlayer.IsStandard) // only standard clients have a MacId
            {
                TimeSpan timeout = TimeSpan.Zero;
                ReadOnlySpan<char> reason = ReadOnlySpan<char>.Empty;

                if (parameters.StartsWith("-t") || parameters.StartsWith("-s"))
                {
                    ReadOnlySpan<char> token = parameters.AsSpan(2).GetToken(' ', out reason);
                    if (token.IsEmpty
                        || !int.TryParse(token, out int numValue))
                    {
                        _chat.SendMessage(p, $"{parameters.AsSpan(0, 2)} was specified, but invalid input for a timeout period.");
                        return;
                    }

                    timeout = TimeSpan.FromSeconds(numValue);
                }
                else if (parameters.StartsWith("-m"))
                {
                    ReadOnlySpan<char> token = parameters.AsSpan(2).GetToken(' ', out reason);
                    if (token.IsEmpty
                        || !int.TryParse(token, out int numValue))
                    {
                        _chat.SendMessage(p, $"{parameters.AsSpan(0, 2)} was specified, but invalid input for a timeout period.");
                        return;
                    }

                    timeout = TimeSpan.FromMinutes(numValue);
                }
                else
                {
                    ReadOnlySpan<char> token = parameters.AsSpan().GetToken(' ', out reason);
                    if (!token.IsEmpty
                        && int.TryParse(token, out int numValue))
                    {
                        timeout = TimeSpan.FromSeconds(numValue);
                    }
                    else
                    {
                        reason = parameters.AsSpan();
                    }
                }

                if (timeout > TimeSpan.Zero)
                {
                    BanRecord ban = new(DateTime.UtcNow + timeout, p.Name, reason.Trim().ToString());

                    lock (_lockObj)
                    {
                        _banDictionary[targetPlayer.MacId] = ban;
                    }

                    _chat.SendMessage(p, $"Kicked '{targetPlayer.Name}' for {timeout}.");
                }
                else
                {
                    _chat.SendMessage(p, $"Kicked '{targetPlayer.Name}'.");
                }
            }
            else
            {
                _chat.SendMessage(p, $"Kicked '{targetPlayer.Name}'.");
            }

            _playerData.KickPlayer(targetPlayer);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Lists the current kicks (machine-id bans) in effect.")]
        private void Command_listkick(string commandName, string parameters, Player p, ITarget target)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                DateTime now = DateTime.UtcNow;

                lock (_lockObj)
                {
                    foreach (var kvp in _banDictionary)
                    {
                        if (sb.Length > 0)
                            sb.Append(", ");

                        TimeSpan difference = kvp.Value.Expire - now;
                        if (difference > TimeSpan.Zero)
                            sb.Append($"{kvp.Key:X} by {kvp.Value.Kicker} ({kvp.Value.Reason}) ({difference} left)");
                        else
                            sb.Append($"{kvp.Key:X} by {kvp.Value.Kicker} ({kvp.Value.Reason}) (kick has expired)");
                    }
                }

                _chat.SendMessage(p, $"Active machine id bans:");
                _chat.SendWrappedText(p, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<machine id>",
            Description = "Removes a machine id ban.")]
        private void Command_delkick(string commandName, string parameters, Player p, ITarget target)
        {
            if (!uint.TryParse(parameters, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint macId))
            {
                _chat.SendMessage(p, "Invalid machine id.");
            }
            else
            {
                bool success;

                lock (_lockObj)
                {
                    success = _banDictionary.Remove(macId);
                }

                if (success)
                    _chat.SendMessage(p, $"Successfully removed ban {macId:X}.");
                else
                    _chat.SendMessage(p, $"Ban {macId:X} not found.");
            }
        }

        #endregion

        public record class BanRecord(DateTime Expire, string Kicker, string Reason)
        {
            /// <summary>
            /// The number attempts after being kicked.
            /// </summary>
            public int Count { get; set; }
        }
    }
}
