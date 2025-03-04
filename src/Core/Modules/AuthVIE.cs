﻿using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that authenticates VIE clients (bots) based on username and IP range.
    /// </summary>
    /// <remarks>
    /// The global.conf should contain a section like this:
    /// <code>
    /// [VIEnames]
    /// SnrrrubSpace = any
    /// pub0bot = 127.0.0.1
    /// Probe1 = 65.72.
    /// </code>
    /// The key is the username.
    /// A value of "any" means any IP address can be used.
    /// Otherwise, value is part or all of an IP address.
    /// <para>
    /// This was based on the auth_vie module in ASSS.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class AuthVIE(
        IAuth auth,
        IConfigManager configManager,
        ILogManager logManager) : IModule, IAuth
    {
        private readonly IAuth _oldAuth = auth ?? throw new ArgumentNullException(nameof(auth));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private InterfaceRegistrationToken<IAuth>? _iAuthToken;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _iAuthToken = broker.RegisterInterface<IAuth>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iAuthToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IAuth

        void IAuth.Authenticate(IAuthRequest authRequest)
        {
            Player? player = authRequest.Player;
            if (player is null)
                return;

            if (player.Type != ClientType.VIE)
            {
                _oldAuth.Authenticate(authRequest);
                return;
            }

            ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)authRequest.LoginPacket.Name).SliceNullTerminated();
            Span<char> nameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
            int decodedCharCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameChars);
            Debug.Assert(decodedCharCount == nameChars.Length);

            string? ipStr = _configManager.GetStr(_configManager.Global, "VIEnames", nameChars);

            if (string.IsNullOrWhiteSpace(ipStr)
                || (!ipStr.Equals("any", StringComparison.OrdinalIgnoreCase) && !IsMatch(ipStr, player.IPAddress!)))
            {
                authRequest.Result.Authenticated = false;
                authRequest.Result.Code = AuthCode.NoPermission2;
                authRequest.Done();

                _logManager.LogP(LogLevel.Malicious, nameof(AuthVIE), player, $"Blocked player {nameChars} from {player.IPAddress}.");
                return;
            }

            _oldAuth.Authenticate(authRequest);
            return;


            static bool IsMatch(string ipStr, IPAddress ipAddress)
            {
                if (string.IsNullOrWhiteSpace(ipStr))
                    return false;

                if (ipAddress is null)
                    return false;

                int maxChars;
                if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
                    maxChars = 15;
                else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    maxChars = 45;
                else
                    return false;

                Span<char> playerIpStr = stackalloc char[maxChars];
                if (!ipAddress.TryFormat(playerIpStr, out int charsWritten))
                    return false;

                playerIpStr = playerIpStr[..charsWritten];
                return playerIpStr.StartsWith(ipStr);
            }
        }

        #endregion
    }
}
