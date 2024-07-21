using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Collections;
using System;
using System.Buffers.Binary;
using System.Net;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the ability to redirect players to another zone server.
    /// </summary>
    [CoreModuleInfo]
    public class Redirect : IModule, IRedirect
    {
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private INetwork _network;

        private InterfaceRegistrationToken<IRedirect> _iRedirectRegistrationToken;

        private readonly Trie<RegisteredRedirect> _redirectCache = new(false);

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICommandManager commandManager,
            IConfigManager configManager,
            INetwork network)
        {
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            _commandManager.AddCommand("redirect", Command_redirect);
            _iRedirectRegistrationToken = broker.RegisterInterface<IRedirect>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iRedirectRegistrationToken);
            _commandManager.RemoveCommand("redirect", Command_redirect);
            return true;
        }

        #endregion

        #region IRedirect

        [ConfigHelp("Redirects", "<name>", ConfigScope.Global, typeof(string),
            Description = "Settings in the Redirects section correspond to arena names. If a " +
            "player tries to ?go to an arena name listed in this section, they " +
            "will be redirected to the zone specified as the value of the " +
            "setting. The format of values is 'ip:port[:arena]'.")]
        bool IRedirect.AliasRedirect(ITarget target, ReadOnlySpan<char> destination)
        {
            if (target == null)
                return false;

            if (!_redirectCache.TryGetValue(destination, out RegisteredRedirect registeredRedirect))
            {
                bool isAlias = true;
                ReadOnlySpan<char> value = _configManager.GetStr(_configManager.Global, "Redirects", destination);
                if (value == null)
                {
                    // If it's not an alias, then maybe it's a literal address.
                    isAlias = false;
                    value = destination;
                }

                // <ip>:<port>[:<arena>]
                ReadOnlySpan<char> remaining = value;
                ReadOnlySpan<char> token;

                // ip
                token = remaining.GetToken(':', out remaining);
                if (token.IsEmpty || token.IsWhiteSpace() || !IPAddress.TryParse(token, out IPAddress address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return false;

                // port
                token = remaining.GetToken(':', out remaining);
                if (token.IsEmpty || token.IsWhiteSpace() || !ushort.TryParse(token, out ushort port))
                    return false;

                // (optional) arena name
                string arenaName = null;
                remaining = remaining.TrimStart(':');
                if (!remaining.IsEmpty && !remaining.IsWhiteSpace())
                {
                    arenaName = remaining.Trim().ToString();
                }

                registeredRedirect = new RegisteredRedirect(new IPEndPoint(address, port), arenaName);

                if (isAlias)
                {
                    _redirectCache.Add(destination, registeredRedirect);
                }
            }

            return ((IRedirect)this).RawRedirect(
                target,
                registeredRedirect.IPEndPoint,
                registeredRedirect.ArenaName != null ? (short)-3 : (short)-1,
                registeredRedirect.ArenaName);
        }

        bool IRedirect.RawRedirect(ITarget target, IPEndPoint ipEndPoint, short arenaType, ReadOnlySpan<char> arenaName)
        {
            if (target == null || ipEndPoint == null)
                return false;

            if (!TryGetIPv4(ipEndPoint.Address, out uint ip))
                return false;

            S2C_Redirect redirect = new(ip, (ushort)ipEndPoint.Port, arenaType, arenaName, 0);
            _network.SendToTarget(target, ref redirect, NetSendFlags.Reliable);
            return true;
        }

        bool IRedirect.ArenaRequest(Player player, ReadOnlySpan<char> arenaName)
        {
            if (player == null)
                return false;

            return ((IRedirect)this).AliasRedirect(player, arenaName);
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<redirect alias> | <ip>:<port>[:<arena>]",
            Description = "Redirects the target to a different zone.")]
        private void Command_redirect(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.Type == TargetType.Arena)
            {
                target = player;
            }

            ((IRedirect)this).AliasRedirect(target, parameters);
        }

        private static bool TryGetIPv4(IPAddress address, out uint ip)
        {
            if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ip = 0;
                return false;
            }

            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out int bytesWritten) || bytesWritten != 4)
            {
                ip = 0;
                return false;
            }

            ip = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }

        #region Helper types

        public class RegisteredRedirect
        {
            public readonly IPEndPoint IPEndPoint;
            public readonly string ArenaName;

            public RegisteredRedirect(IPEndPoint ipEndPoint, string arenaName)
            {
                IPEndPoint = ipEndPoint;
                ArenaName = arenaName;
            }
        }

        #endregion
    }
}
