using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality for temporarily kicking players off.
    /// This module addes the ?kick command and other associated commands to manage bans.
    /// </summary>
    [CoreModuleInfo]
    public class AuthBan : IModule, IAuth
    {
        private IAuth _oldAuth;
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<IAuth> _iAuthToken;

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
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
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
            if (broker.UnregisterInterface(ref _iAuthToken) != 0)
                return false;

            _commandManager.RemoveCommand("kick", Command_kick);
            _commandManager.RemoveCommand("listkick", Command_listkick);
            _commandManager.RemoveCommand("listmidbans", Command_listkick);
            _commandManager.RemoveCommand("delkick", Command_delkick);
            _commandManager.RemoveCommand("liftkick", Command_delkick);
            _commandManager.RemoveCommand("delmidban", Command_delkick);

            return true;
        }

        #endregion

        void IAuth.Authenticate(IAuthRequest authRequest)
        {
            if (authRequest is null)
                return;

            Player player = authRequest.Player;
            if (player is null
                || authRequest.LoginBytes.Length < LoginPacket.VIELength)
            {
                authRequest.Result.Code = AuthCode.CustomText;
                authRequest.Result.SetCustomText("Internal server error.");
                return;
            }

            ref readonly LoginPacket loginPacket = ref authRequest.LoginPacket;
            bool handled = false;

            lock (_lockObj)
            {
                if (player.IsStandard // only standard clients have a MacId
                    && _banDictionary.TryGetValue(loginPacket.MacId, out BanRecord ban))
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < ban.Expire)
                    {
                        authRequest.Result.Code = AuthCode.CustomText;

                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            if (string.IsNullOrWhiteSpace(ban.Reason))
                                sb.Append($"You have been temporarily kicked. You may log in again in {ban.Expire - now}.");
                            else
                                sb.Append($"You have been temporarily kicked for {ban.Reason}. You may log in again in {ban.Expire - now}.");

                            Span<char> text = stackalloc char[Math.Min(authRequest.Result.GetMaxCustomTextLength(), sb.Length)];
                            sb.CopyTo(0, text, text.Length);
                            authRequest.Result.SetCustomText(text);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }

                        handled = true;

                        ban.Count++;

                        ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)authRequest.LoginPacket.Name).SliceNullTerminated();
                        Span<char> name = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
                        int decodedCharCount = StringUtils.DefaultEncoding.GetChars(nameBytes, name);
                        Debug.Assert(name.Length == decodedCharCount);

                        _logManager.LogM(LogLevel.Info, nameof(AuthBan), $"Player [{name}] tried to login (try {ban.Count}), banned for {ban.Expire - now} longer.");
                    }
                    else
                    {
                        _banDictionary.Remove(loginPacket.MacId);
                    }
                }
            }

            if (handled)
            {
                authRequest.Done();
            }
            else
            {
                _oldAuth.Authenticate(authRequest);
            }
        }

        #region Command handlers

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "[-s seconds | -t seconds | -m minutes | seconds] [reason]",
            Description = """
                Kicks the player off of the server, with an optional timeout. (-s number, -t number, or number for seconds, -m number for minutes.)
                For kicks with a timeout, you may provide a message to be displayed to the user.
                Messages appear to users on timeout as "You have been temporarily kicked for <reason>."
                """)]
        private void Command_kick(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, "This comand only operates when targeting a specific player.");
                return;
            }

            if (targetPlayer == player)
                return;

            if (!_capabilityManager.HigherThan(player, targetPlayer))
            {
                _chat.SendMessage(player, $"You don't have permission to use ?kick on {targetPlayer.Name}.");
                _chat.SendMessage(targetPlayer, $"{player.Name} tried to use ?kick on you.");
                return;
            }

            if (targetPlayer.IsStandard) // only standard clients have a MacId
            {
                TimeSpan timeout = TimeSpan.Zero;
                ReadOnlySpan<char> reason = ReadOnlySpan<char>.Empty;

                if (parameters.StartsWith("-t") || parameters.StartsWith("-s"))
                {
                    ReadOnlySpan<char> token = parameters[2..].GetToken(' ', out reason);
                    if (token.IsEmpty
                        || !int.TryParse(token, out int numValue))
                    {
                        _chat.SendMessage(player, $"{parameters[..2]} was specified, but invalid input for a timeout period.");
                        return;
                    }

                    timeout = TimeSpan.FromSeconds(numValue);
                }
                else if (parameters.StartsWith("-m"))
                {
                    ReadOnlySpan<char> token = parameters[2..].GetToken(' ', out reason);
                    if (token.IsEmpty
                        || !int.TryParse(token, out int numValue))
                    {
                        _chat.SendMessage(player, $"{parameters[..2]} was specified, but invalid input for a timeout period.");
                        return;
                    }

                    timeout = TimeSpan.FromMinutes(numValue);
                }
                else
                {
                    ReadOnlySpan<char> token = parameters.GetToken(' ', out reason);
                    if (!token.IsEmpty
                        && int.TryParse(token, out int numValue))
                    {
                        timeout = TimeSpan.FromSeconds(numValue);
                    }
                    else
                    {
                        reason = parameters;
                    }
                }

                if (timeout > TimeSpan.Zero)
                {
                    BanRecord ban = new(DateTime.UtcNow + timeout, player.Name, reason.Trim().ToString());

                    lock (_lockObj)
                    {
                        _banDictionary[targetPlayer.MacId] = ban;
                    }

                    _chat.SendMessage(player, $"Kicked '{targetPlayer.Name}' for {timeout}.");
                }
                else
                {
                    _chat.SendMessage(player, $"Kicked '{targetPlayer.Name}'.");
                }
            }
            else
            {
                _chat.SendMessage(player, $"Kicked '{targetPlayer.Name}'.");
            }

            _playerData.KickPlayer(targetPlayer);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Lists the current kicks (machine-id bans) in effect.")]
        private void Command_listkick(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
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

                _chat.SendMessage(player, $"Active machine id bans:");
                _chat.SendWrappedText(player, sb);
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
        private void Command_delkick(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!uint.TryParse(parameters, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint macId))
            {
                _chat.SendMessage(player, "Invalid machine id.");
            }
            else
            {
                bool success;

                lock (_lockObj)
                {
                    success = _banDictionary.Remove(macId);
                }

                if (success)
                    _chat.SendMessage(player, $"Successfully removed ban {macId:X}.");
                else
                    _chat.SendMessage(player, $"Ban {macId:X} not found.");
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
