using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Security.Cryptography;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that authenticates players based on hashed passwords stored in a configuration file ('conf/passwd.conf').
    /// </summary>
    public sealed class AuthFile : IModule, IAuth, IDisposable
    {
        private IPlayerData playerData;
        private IConfigManager config;
        private ICommandManager commandManager;
        private IChat chat;
        private ILogManager log;
        private InterfaceRegistrationToken iAuthToken;

        private ConfigHandle pwdFile;
        private HashAlgorithm hashAlgorithm;
        private HashEncoding hashEncoding;
        private readonly object hashLock = new object();
        private bool allowUnknown;
        private int pdKey;

        private const string ConfigFileName = "passwd.conf";

        [ConfigHelp("General", "HashAlgorithm", ConfigScope.Global, ConfigFileName, typeof(string), DefaultValue = "MD5", 
            Description = "The algorithm to use for hashing passwords. See .NET documentation on 'HashAlgorithm.Create'.")]
        [ConfigHelp("General", "HashEncoding", ConfigScope.Global, ConfigFileName, typeof(string), DefaultValue = "hex", 
            Description = "How password hashes are encoded in the password file. hex|Base64")]
        [ConfigHelp("General", "AllowUnknown", ConfigScope.Global, ConfigFileName, typeof(bool), DefaultValue = "1", 
            Description = "Determines whether to allow players not listed in the password file.")]
        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            IConfigManager config,
            ICommandManager commandManager,
            IChat chat,
            ILogManager log)
        {
            this.playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            pwdFile = config.OpenConfigFile(null, ConfigFileName);
            if (pwdFile == null)
            {
                log.LogM(LogLevel.Error, nameof(AuthFile), "Module could not load due to being unable to open passwd.conf.");
                return false;
            }

            string hashAlgoritmName = config.GetStr(pwdFile, "General", "HashAlgorithm");
            hashAlgorithm = string.IsNullOrWhiteSpace(hashAlgoritmName)
                ? MD5.Create()
                : HashAlgorithm.Create(hashAlgoritmName);

            if (hashAlgorithm == null)
            {
                hashAlgorithm = MD5.Create();

                if (hashAlgorithm == null)
                {
                    log.LogM(LogLevel.Error, nameof(AuthFile), "Invalid General:HashAlgorithm specified, unable to proceed.");
                    return false;
                }
                else
                {
                    log.LogM(LogLevel.Warn, nameof(AuthFile), "Invalid General:HashAlgorithm specified, defaulted to MD5.");
                }
            }

            string hashEncodingStr = config.GetStr(pwdFile, "General", "HashEncoding");
            hashEncoding = string.Equals(hashEncodingStr, "Base64", StringComparison.OrdinalIgnoreCase)
                 ? HashEncoding.Base64
                 : HashEncoding.Hexadecimal;

            allowUnknown = config.GetInt(pwdFile, "General", "AllowUnknown", 1) != 0;

            pdKey = playerData.AllocatePlayerData<PlayerData>();

            commandManager.AddCommand("passwd", Command_passwd);
            commandManager.AddCommand("local_password", Command_passwd);
            commandManager.AddCommand("addallowed", Command_addallowed);
            commandManager.AddCommand("set_local_password", Command_set_local_password);
            commandManager.AddUnlogged("passwd");
            commandManager.AddUnlogged("local_password");

            iAuthToken = broker.RegisterInterface<IAuth>(this);
            // TODO: billing fallback

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IAuth>(ref iAuthToken) != 0)
                return false;

            commandManager.RemoveCommand("passwd", Command_passwd);
            commandManager.RemoveCommand("local_password", Command_passwd);
            commandManager.RemoveCommand("addallowed", Command_addallowed);
            commandManager.RemoveCommand("set_local_password", Command_set_local_password);
            commandManager.RemoveUnlogged("passwd");
            commandManager.RemoveUnlogged("local_password");

            playerData.FreePlayerData(pdKey);

            config.CloseConfigFile(pwdFile);
            pwdFile = null;

            hashAlgorithm.Dispose();
            hashAlgorithm = null;

            return true;
        }

        void IAuth.Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            if (p[pdKey] is not PlayerData pd)
                return;

            string name = lp.Name;
            string pwd = StringUtils.ReadNullTerminatedString(lp.PasswordSpan);
            pd.PasswordHash = GetPasswordHash(name, pwd);

            AuthData authData = new AuthData()
            {
                Authenticated = false,
                Name = name,
                SendName = name,
            };

            string line = config.GetStr(pwdFile, "users", name);

            if (line != null)
            {
                if (string.Equals(line, "lock"))
                {
                    authData.Code = AuthCode.NoPermission;
                }
                else if (string.Equals(line, "any"))
                {
                    authData.Code = AuthCode.OK;
                }
                else
                {
                    if (!string.Equals(pd.PasswordHash, line))
                    {
                        authData.Code = AuthCode.BadPassword;
                    }
                    else
                    {
                        // only a correct password gets marked as authenticated
                        authData.Authenticated = true;
                        authData.Code = AuthCode.OK;
                    }
                }
            }
            else
            {
                // no match found
                authData.Code = allowUnknown ? AuthCode.OK : AuthCode.NoPermission;
            }

            done(p, authData);
        }

        private string GetPasswordHash(ReadOnlySpan<char> name, ReadOnlySpan<char> pwd)
        {
            if (name.Length == 0 || name.Length > 24)
                throw new ArgumentOutOfRangeException(nameof(name));

            if (pwd.Length == 0 || pwd.Length > 32)
                throw new ArgumentOutOfRangeException(nameof(pwd));

            lock (hashLock)
            {
                // ignore case on name
                Span<char> lowerName = stackalloc char[name.Length];
                MemoryExtensions.ToLowerInvariant(name, lowerName);
                
                // combine name and password into bytes that we'll hash
                Span<byte> data = stackalloc byte[56];
                data.Clear();
                Encoding.ASCII.GetBytes(lowerName, data.Slice(0, 24));
                Encoding.ASCII.GetBytes(pwd, data.Slice(24, 32));

                // get the hash
                Span<byte> hashSpan = stackalloc byte[hashAlgorithm.HashSize / 8];
                if (hashAlgorithm.TryComputeHash(data, hashSpan, out int bytesWritten))
                {
                    return EncodeAsString(hashSpan);
                }
            }

            return null;
        }

        private string EncodeAsString(ReadOnlySpan<byte> hashSpan)
        {
            return hashEncoding == HashEncoding.Base64
                ? Convert.ToBase64String(hashSpan, Base64FormattingOptions.None)
                : EncodeAsHex(hashSpan);
        }

        private static string EncodeAsHex(ReadOnlySpan<byte> data)
        {
            const string table = "0123456789abcdef";

            Span<char> hashChars = stackalloc char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                hashChars[i * 2 + 0] = table[(data[i] & 0xf0) >> 4];
                hashChars[i * 2 + 1] = table[(data[i] & 0x0f) >> 0];
            }

            return hashChars.ToString();
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Args = "<new password>",
            Description =
            "Changes your local server password. Note that this command only changes\n" +
            "the password used by the auth_file authentication mechanism (used when the\n" +
            "billing server is disconnected). This command does not involve the billing\n" +
            "server.\n")]
        [ConfigHelp("General", "RequireAuthenticationToSetPassword", ConfigScope.Global, ConfigFileName, typeof(bool), DefaultValue = "1",
            Description = "If true, you must be authenticated (have used a correct password) according to this module or some other module before using ?local_password to change your local password.")]
        private void Command_passwd(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                chat.SendMessage(p, "You must specify a password.");
            }
            else
            {
                if (config.GetInt(pwdFile, "General", "RequireAuthenticationToSetPassword", 1) != 0
                    && !p.Flags.Authenticated)
                {
                    chat.SendMessage(p, "You must be authenticated to change your local password.");
                }
                else
                {
                    string hex = GetPasswordHash(p.Name, parameters);
                    config.SetStr(pwdFile, "users", p.Name, hex, null, true);
                    chat.SendMessage(p, "Password set.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Args = "<player name>", 
            Description =
            "Adds a player to passwd.conf with no set password. This will allow them\n" +
            "to log in when AllowUnknown is set to false, and has no use otherwise.\n")]
        private void Command_addallowed(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                chat.SendMessage(p, "You must specify a player name.");
                return;
            }

            config.SetStr(pwdFile, "users", parameters, "any", $"added by {p.Name} on {DateTime.UtcNow}", true);
            chat.SendMessage(p, $"Added {parameters} to the allowed player list.");
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description =
            "If used on a player that has no local password set, it will set their\n" +
            "local password to the password they used to log in to this session.\n")]
        private void Command_set_local_password(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Player)
            {
                chat.SendMessage(p, "You must use this on a player.");
                return;
            }

            Player targetPlayer = ((IPlayerTarget)target).Player;

            string hash = config.GetStr(pwdFile, "users", targetPlayer.Name);

            if (!string.IsNullOrWhiteSpace(hash))
            {
                chat.SendMessage(p, $"{targetPlayer.Name} has already set a local password.");
                return;
            }

            if (targetPlayer[pdKey] is not PlayerData pd
                || string.IsNullOrWhiteSpace(pd.PasswordHash))
            {
                chat.SendMessage(p, "Hashed password missing.");
                return;
            }

            config.SetStr(pwdFile, "users", targetPlayer.Name, pd.PasswordHash, $"added by {p.Name} on {DateTime.UtcNow}", true);
            chat.SendMessage(p, $"Set local password for {targetPlayer.Name}");
            chat.SendMessage(targetPlayer, $"Your password has been set as a local password by {p.Name}.");
        }

        public void Dispose()
        {
            if (hashAlgorithm != null)
            {
                hashAlgorithm.Dispose();
                hashAlgorithm = null;
            }
        }

        private class PlayerData
        {
            public string PasswordHash = null;
        }

        private enum HashEncoding
        {
            Hexadecimal,
            Base64,
        }
    }
}
