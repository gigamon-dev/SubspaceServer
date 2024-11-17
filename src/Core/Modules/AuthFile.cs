using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GlobalPasswdSettings = SS.Core.ConfigHelp.Constants.GlobalPasswd;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that authenticates players based on hashed passwords stored in a configuration file ('conf/passwd.conf').
    /// This can also be used along with the <see cref="BillingUdp"/> to provide authentication when the the billing server connection is down.
    /// </summary>
    [CoreModuleInfo]
    public sealed class AuthFile(
        IPlayerData playerData,
        IConfigManager configManager,
        ICommandManager commandManager,
        IChat chat,
        ILogManager logManager) : IAsyncModule, IAuth, IBillingFallback, IDisposable
    {
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private InterfaceRegistrationToken<IAuth>? _iAuthToken;
        private InterfaceRegistrationToken<IBillingFallback>? _iBillingFallbackToken;

        private ConfigHandle? _pwdFile;
        private HashAlgorithm? _hashAlgorithm;
        private HashEncoding _hashEncoding;
        private int _encodedHashLength;
        private readonly Lock _hashLock = new();
        private bool _allowUnknown;
        private PlayerDataKey<PlayerData> _pdKey;

        private const string ConfigFileName = "passwd.conf";

        #region Module members

        [ConfigHelp("General", "HashAlgorithm", ConfigScope.Global, ConfigFileName, Default = "MD5",
            Description = "The algorithm to use for hashing passwords. Supported algorithms include: MD5, SHA256, and SHA512.")]
        [ConfigHelp("General", "HashEncoding", ConfigScope.Global, ConfigFileName, Default = "hex",
            Description = "How password hashes are encoded in the password file. hex|Base64")]
        [ConfigHelp<bool>("General", "AllowUnknown", ConfigScope.Global, ConfigFileName, Default = true,
            Description = "Determines whether to allow players not listed in the password file.")]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _pwdFile = await configManager.OpenConfigFileAsync(null, ConfigFileName).ConfigureAwait(false);
            if (_pwdFile == null)
            {
                logManager.LogM(LogLevel.Error, nameof(AuthFile), "Module could not load due to being unable to open passwd.conf.");
                return false;
            }

            string? hashAlgorithmName = configManager.GetStr(_pwdFile, "General", "HashAlgorithm");
            if (string.IsNullOrWhiteSpace(hashAlgorithmName))
            {
                hashAlgorithmName = GlobalPasswdSettings.General.HashAlgorithm.Default;
            }

            _hashAlgorithm = hashAlgorithmName switch
            {
                "MD5" => MD5.Create(),
                "SHA256" => SHA256.Create(),
                "SHA512" => SHA512.Create(),
                _ => null,
            };

            if (_hashAlgorithm is null)
            {
                _hashAlgorithm = MD5.Create();

                if (_hashAlgorithm is null)
                {
                    logManager.LogM(LogLevel.Error, nameof(AuthFile), "Invalid General:HashAlgorithm specified, unable to proceed.");
                    return false;
                }
                else
                {
                    logManager.LogM(LogLevel.Warn, nameof(AuthFile), "Invalid General:HashAlgorithm specified, defaulted to MD5.");
                }
            }

            string? hashEncodingStr = configManager.GetStr(_pwdFile, "General", "HashEncoding");
            _hashEncoding = string.Equals(hashEncodingStr, "Base64", StringComparison.OrdinalIgnoreCase)
                 ? HashEncoding.Base64
                 : HashEncoding.Hexadecimal;

            _encodedHashLength = GetEncodedHashLength();

            _allowUnknown = configManager.GetBool(_pwdFile, "General", "AllowUnknown", GlobalPasswdSettings.General.AllowUnknown.Default);

            _pdKey = playerData.AllocatePlayerData<PlayerData>();

            commandManager.AddCommand("passwd", Command_passwd);
            commandManager.AddCommand("local_password", Command_passwd);
            commandManager.AddCommand("addallowed", Command_addallowed);
            commandManager.AddCommand("set_local_password", Command_set_local_password);
            commandManager.AddUnlogged("passwd");
            commandManager.AddUnlogged("local_password");

            _iAuthToken = broker.RegisterInterface<IAuth>(this);
            _iBillingFallbackToken = broker.RegisterInterface<IBillingFallback>(this);

            return true;

            int GetEncodedHashLength()
            {
                int hashByteLength = _hashAlgorithm.HashSize / 8;

                if (_hashEncoding == HashEncoding.Base64)
                {
                    int encodedLength = (int)(4.0d / 3.0d * hashByteLength);
                    if (encodedLength % 4 != 0)
                    {
                        encodedLength += 4 - (encodedLength % 4);
                    }

                    return encodedLength;
                }
                else
                {
                    return hashByteLength * 2;
                }
            }
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iBillingFallbackToken) != 0)
                return Task.FromResult(false);

            if (broker.UnregisterInterface(ref _iAuthToken) != 0)
                return Task.FromResult(false);

            _commandManager.RemoveCommand("passwd", Command_passwd);
            _commandManager.RemoveCommand("local_password", Command_passwd);
            _commandManager.RemoveCommand("addallowed", Command_addallowed);
            _commandManager.RemoveCommand("set_local_password", Command_set_local_password);
            _commandManager.RemoveUnlogged("passwd");
            _commandManager.RemoveUnlogged("local_password");

            _playerData.FreePlayerData(ref _pdKey);

            if (_pwdFile is not null)
            {
                _configManager.CloseConfigFile(_pwdFile);
                _pwdFile = null;
            }

            if (_hashAlgorithm is not null)
            {
                _hashAlgorithm.Dispose();
                _hashAlgorithm = null;
            }

            return Task.FromResult(true);
        }

        #endregion

        #region IAuth

        void IAuth.Authenticate(IAuthRequest authRequest)
        {
            if (_pwdFile is null)
                throw new InvalidOperationException("Not loaded.");

            Player? player = authRequest.Player;
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData? playerData)
                || authRequest.LoginBytes.Length < LoginPacket.VIELength)
            {
                authRequest.Result.Code = AuthCode.CustomText;
                authRequest.Result.SetCustomText("Internal server error.");
                authRequest.Done();
                return;
            }

            ref readonly LoginPacket lp = ref authRequest.LoginPacket;

            ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)lp.Name).SliceNullTerminated();
            Span<char> nameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
            int decodedCharCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameChars);
            Debug.Assert(decodedCharCount == nameChars.Length);

            // Extra name cleanup and check even though the Core module should have taken care of it.
            nameChars = nameChars.Trim();
            if (nameChars.IsEmpty)
            {
                authRequest.Result.Code = AuthCode.BadName;
                authRequest.Done();
                return;
            }

            ReadOnlySpan<byte> passwordBytes = ((ReadOnlySpan<byte>)lp.Password).SliceNullTerminated();
            Span<char> passwordChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(passwordBytes)];
            decodedCharCount = StringUtils.DefaultEncoding.GetChars(passwordBytes, passwordChars);
            Debug.Assert(decodedCharCount == passwordChars.Length);

            Span<char> encodedHash = stackalloc char[_encodedHashLength];
            if (!TryGetPasswordHash(nameChars, passwordChars, ref encodedHash))
            {
                authRequest.Result.Code = AuthCode.CustomText;
                authRequest.Result.SetCustomText("Internal server error.");
                authRequest.Done();
                return;
            }

            playerData.SetPasswordHash(encodedHash);

            IAuthResult result = authRequest.Result;
            result.Authenticated = false;
            result.SetName(nameChars);
            result.SetSendName(nameChars);

            string? line = _configManager.GetStr(_pwdFile, "users", nameChars);

            if (line is not null)
            {
                if (string.Equals(line, "lock"))
                {
                    result.Code = AuthCode.NoPermission;
                }
                else if (string.Equals(line, "any"))
                {
                    result.Code = AuthCode.OK;
                }
                else
                {
                    if (!playerData.PasswordHash.Equals(line, StringComparison.Ordinal))
                    {
                        result.Code = AuthCode.BadPassword;
                    }
                    else
                    {
                        // only a correct password gets marked as authenticated
                        result.Authenticated = true;
                        result.Code = AuthCode.OK;
                    }
                }
            }
            else
            {
                // no match found
                result.Code = _allowUnknown ? AuthCode.OK : AuthCode.NoPermission;
            }

            authRequest.Done();
        }

        #endregion

        #region IBillingFallback

        void IBillingFallback.Check<T>(Player player, ReadOnlySpan<char> name, ReadOnlySpan<char> password, BillingFallbackDoneDelegate<T> done, T state)
        {
            if (_pwdFile is null)
                throw new InvalidOperationException("Not loaded.");

            if (name.Length == 0 || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
            {
                done(state, BillingFallbackResult.Mismatch);
                return;
            }

            Span<char> encodedHash = stackalloc char[_encodedHashLength];
            if (!TryGetPasswordHash(name, password, ref encodedHash))
            {
                done(state, BillingFallbackResult.Mismatch);
                return;
            }

            playerData.SetPasswordHash(encodedHash);

            string? line = _configManager.GetStr(_pwdFile, "users", name);
            if (line != null)
            {
                if (string.Equals(line, "lock", StringComparison.OrdinalIgnoreCase))
                {
                    done(state, BillingFallbackResult.Mismatch);
                    return;
                }

                if (string.Equals(line, "any", StringComparison.OrdinalIgnoreCase))
                {
                    done(state, BillingFallbackResult.Match);
                    return;
                }

                if (playerData.PasswordHash.Equals(line, StringComparison.Ordinal))
                {
                    done(state, BillingFallbackResult.Match);
                    return;
                }

                done(state, BillingFallbackResult.Mismatch);
                return;
            }

            bool allow = _configManager.GetInt(_pwdFile, "General", "AllowUnknown", 1) != 0;

            if (allow)
                done(state, BillingFallbackResult.NotFound);
            else
                done(state, BillingFallbackResult.Mismatch);
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<new password>",
            Description = $"""
                Changes your local server password. Note that this command only changes
                the password used by the {nameof(AuthFile)} authentication mechanism (used when the
                billing server is disconnected). This command does not involve the billing 
                server.
                """)]
        [ConfigHelp<bool>("General", "RequireAuthenticationToSetPassword", ConfigScope.Global, ConfigFileName, Default = true,
            Description = "If true, you must be authenticated (have used a correct password) according to this module or some other module before using ?local_password to change your local password.")]
        private void Command_passwd(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_pwdFile is null)
                throw new InvalidOperationException("Not loaded.");

            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "You must specify a password.");
            }
            else
            {
                if (_configManager.GetBool(_pwdFile, "General", "RequireAuthenticationToSetPassword", GlobalPasswdSettings.General.RequireAuthenticationToSetPassword.Default)
                    && !player.Flags.Authenticated)
                {
                    _chat.SendMessage(player, "You must be authenticated to change your local password.");
                }
                else
                {
                    Span<char> encodedHash = stackalloc char[_encodedHashLength];
                    if (!TryGetPasswordHash(player.Name, parameters, ref encodedHash))
                    {
                        return;
                    }

                    _configManager.SetStr(_pwdFile, "users", player.Name!, encodedHash.ToString(), null, true);
                    _chat.SendMessage(player, "Password set.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<player name>",
            Description = """
                Adds a player to passwd.conf with no set password. This will allow them
                to log in when AllowUnknown is set to false, and has no use otherwise.
                """)]
        private void Command_addallowed(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_pwdFile is null)
                throw new InvalidOperationException("Not loaded.");

            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "You must specify a player name.");
                return;
            }

            string? hash = _configManager.GetStr(_pwdFile, "users", parameters);

            if (!string.IsNullOrWhiteSpace(hash))
            {
                if (string.Equals(hash, "any", StringComparison.OrdinalIgnoreCase))
                {
                    _chat.SendMessage(player, $"{parameters} is already allowed.");
                    return;
                }
                else if (!string.Equals(hash, "lock", StringComparison.OrdinalIgnoreCase))
                {
                    _chat.SendMessage(player, $"{parameters} already has a local password set.");
                    return;
                }
            }

            _configManager.SetStr(_pwdFile, "users", parameters.ToString(), "any", $"added by {player.Name} on {DateTime.UtcNow}", true);
            _chat.SendMessage(player, $"Added {parameters} to the allowed player list.");
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = """
                If used on a player that has no local password set, it will set their
                local password to the password they used to log in to this session.
                """)]
        private void Command_set_local_password(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (_pwdFile is null)
                throw new InvalidOperationException("Not loaded.");

            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                _chat.SendMessage(player, "You must use this on a player.");
                return;
            }

            string? hash = _configManager.GetStr(_pwdFile, "users", targetPlayer.Name);

            if (!string.IsNullOrWhiteSpace(hash) && !string.Equals(hash, "any", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"{targetPlayer.Name} has already set a local password.");
                return;
            }

            if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData)
                || targetPlayerData.PasswordHash.IsEmpty)
            {
                _chat.SendMessage(player, "Hashed password missing.");
                return;
            }

            _configManager.SetStr(_pwdFile, "users", targetPlayer.Name!, targetPlayerData.PasswordHash.ToString(), $"added by {player.Name} on {DateTime.UtcNow}", true);
            _chat.SendMessage(player, $"Set local password for {targetPlayer.Name}");
            _chat.SendMessage(targetPlayer, $"Your password has been set as a local password by {player.Name}.");
        }

        #endregion

        private bool TryGetPasswordHash(ReadOnlySpan<char> name, ReadOnlySpan<char> pwd, ref Span<char> hashChars)
        {
            // Limit name to 23 characters to stay compatible with ASSS hashes.
            if (name.Length > 23)
            {
                // ASSS uses a buffer of 24 bytes, with a null-terminator.
                name = name[..23];
            }

            // Limit password to 31 characters to stay compatible with ASSS hashes.
            if (pwd.Length > 31)
            {
                // ASSS uses a buffer of 32 bytes, with a null-terminator.
                pwd = pwd[..31];
            }

            // Ignore case on the name.
            Span<char> lowerName = stackalloc char[name.Length];
            if (name.ToLowerInvariant(lowerName) == -1)
                return false;

            // Combine name and password into bytes that we'll hash.
            Span<byte> data = stackalloc byte[56];
            data.Clear();

            int encodedBytes = StringUtils.DefaultEncoding.GetBytes(lowerName, data.Slice(0, 24));
            if (encodedBytes != lowerName.Length)
                return false;

            encodedBytes = StringUtils.DefaultEncoding.GetBytes(pwd, data.Slice(24, 32));
            if (encodedBytes != pwd.Length)
                return false;

            // Get the hash.
            Span<byte> hashSpan = stackalloc byte[_hashAlgorithm!.HashSize / 8];
            lock (_hashLock) // HashAlgorithm is not thread-safe
            {
                if (!_hashAlgorithm.TryComputeHash(data, hashSpan, out int bytesWritten))
                    return false;
            }

            // Encode to text.
            int charsWritten;
            if (_hashEncoding == HashEncoding.Base64)
            {
                if (!Convert.TryToBase64Chars(hashSpan, hashChars, out charsWritten, Base64FormattingOptions.None))
                    return false;
            }
            else
            {
                if (!TryEncodeAsHex(hashSpan, hashChars, out charsWritten))
                    return false;
            }

            if (hashChars.Length > charsWritten)
                hashChars = hashChars[..charsWritten];

            return true;

            static bool TryEncodeAsHex(ReadOnlySpan<byte> data, Span<char> hashChars, out int charsWritten)
            {
                const string table = "0123456789abcdef";

                if (hashChars.Length < data.Length * 2)
                {
                    charsWritten = 0;
                    return false;
                }

                for (int i = 0; i < data.Length; i++)
                {
                    hashChars[i * 2 + 0] = table[(data[i] & 0xf0) >> 4];
                    hashChars[i * 2 + 1] = table[(data[i] & 0x0f) >> 0];
                }

                charsWritten = data.Length * 2;
                return true;
            }
        }

        public void Dispose()
        {
            if (_hashAlgorithm != null)
            {
                _hashAlgorithm.Dispose();
                _hashAlgorithm = null;
            }
        }

        #region Helper types

        private class PlayerData : IResettable
        {
            private char[]? _passwordHashChars = null;
            private int _passwordHashLength = 0;

            public ReadOnlySpan<char> PasswordHash => _passwordHashChars is null
                ? ReadOnlySpan<char>.Empty
                : new ReadOnlySpan<char>(_passwordHashChars, 0, _passwordHashLength);

            public void SetPasswordHash(ReadOnlySpan<char> value)
            {
                if (_passwordHashChars is not null && _passwordHashChars.Length < value.Length)
                {
                    ArrayPool<char>.Shared.Return(_passwordHashChars, true);
                    _passwordHashChars = null;
                }

                _passwordHashChars ??= ArrayPool<char>.Shared.Rent(value.Length);
                value.CopyTo(_passwordHashChars);
                _passwordHashLength = value.Length;
            }

            bool IResettable.TryReset()
            {
                if (_passwordHashChars is not null)
                {
                    ArrayPool<char>.Shared.Return(_passwordHashChars, true);
                    _passwordHashChars = null;
                }

                _passwordHashLength = 0;
                return true;
            }
        }

        private enum HashEncoding
        {
            Hexadecimal,
            Base64,
        }

        #endregion
    }
}
