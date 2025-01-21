using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;

namespace SS.Core.Modules
{
	/// <summary>
	/// Module that provides the ability to redirect players to another zone server.
	/// </summary>
	[CoreModuleInfo]
    public sealed class Redirect : IModule, IRedirect
    {
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly INetwork _network;

        private InterfaceRegistrationToken<IRedirect>? _iRedirectRegistrationToken;

        private readonly Dictionary<string, RegisteredRedirect> _redirectCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RegisteredRedirect>.AlternateLookup<ReadOnlySpan<char>> _redirectCacheLookup;

        public Redirect(
            ICommandManager commandManager,
            IConfigManager configManager,
            INetwork network)
        {
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            _redirectCacheLookup = _redirectCache.GetAlternateLookup<ReadOnlySpan<char>>();
		}

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _commandManager.AddCommand("redirect", Command_redirect);
            _iRedirectRegistrationToken = broker.RegisterInterface<IRedirect>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iRedirectRegistrationToken);
            _commandManager.RemoveCommand("redirect", Command_redirect);
            return true;
        }

        #endregion

        #region IRedirect

        [ConfigHelp("Redirects", "_name_", ConfigScope.Global,
            Description = """
                Settings in the Redirects section correspond to arena names. If a
                player tries to ?go to an arena name listed in this section, they
                will be redirected to the zone specified as the value of the
                setting. The format of values is 'ip:port[:arena]'.
                """)]
        bool IRedirect.AliasRedirect(ITarget target, ReadOnlySpan<char> destination)
        {
            if (target is null)
                return false;
            
            if (_redirectCacheLookup.TryGetValue(destination, out RegisteredRedirect? registeredRedirect))
            {
				return RawRedirect(
				    target,
				    registeredRedirect.IP,
				    registeredRedirect.Port,
					registeredRedirect.ArenaName is null ? (short)-1 : (short)-3,
					registeredRedirect.ArenaName);
			}

			bool isAlias = true;
			ReadOnlySpan<char> value = _configManager.GetStr(_configManager.Global, "Redirects", destination);
			if (value.IsEmpty)
			{
				// If it's not an alias, then maybe it's a literal address.
				isAlias = false;
				value = destination;
			}

			// <ip>:<port>[:<arena>]
			Span<Range> ranges = stackalloc Range[3];
			int numRanges = value.Split(ranges, ':', StringSplitOptions.None);
			if (numRanges < 2)
				return false;

			// ip
			ReadOnlySpan<char> ipSpan = value[ranges[0]];
			if (ipSpan.IsEmpty || ipSpan.IsWhiteSpace() || !IPAddress.TryParse(ipSpan, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !TryGetIPv4(address, out uint ip))
				return false;

			// port
			ReadOnlySpan<char> portSpan = value[ranges[1]];
			if (portSpan.IsEmpty || portSpan.IsWhiteSpace() || !ushort.TryParse(portSpan, out ushort port))
				return false;

			// (optional) arena name
			ReadOnlySpan<char> arenaName = (numRanges == 3) ? value[ranges[2]] : [];

			if (isAlias)
			{
                _redirectCacheLookup.TryAdd(destination, new RegisteredRedirect(ip, port, arenaName.ToString()));
			}

			return RawRedirect(
				target,
				ip,
                port,
				arenaName.IsEmpty ? (short)-1 : (short)-3,
				arenaName);
		}

        bool IRedirect.RawRedirect(ITarget target, IPEndPoint ipEndPoint, short arenaType, ReadOnlySpan<char> arenaName)
		{
			if (target == null || ipEndPoint == null)
				return false;

			if (!TryGetIPv4(ipEndPoint.Address, out uint ip))
				return false;

			return RawRedirect(target, ip, (ushort)ipEndPoint.Port, arenaType, arenaName);
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

		private bool RawRedirect(ITarget target, uint ip, ushort port, short arenaType, ReadOnlySpan<char> arenaName)
		{
			S2C_Redirect redirect = new(ip, port, arenaType, arenaName, 0);
			_network.SendToTarget(target, ref redirect, NetSendFlags.Reliable);
			return true;
		}

		#region Helper types

		public class RegisteredRedirect(uint ip, ushort port, string? arenaName)
		{
            public readonly uint IP = ip;
            public readonly ushort Port = port;
            public readonly string? ArenaName = arenaName;
		}

		#endregion
	}
}
