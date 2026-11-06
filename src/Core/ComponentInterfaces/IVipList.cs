using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that decides which players are VIPs (vip.txt).
    /// </summary>
    /// <remarks>
    /// A VIP is trusted to run a non-standard client: a Continuum pre-release, a modified client or a bot.
    /// A VIP's client is told to skip its exe and code checksum checks (S2C 0x0A, byte 6),
    /// the server does not kick for an exe checksum mismatch in the security check,
    /// and a client-reported <see cref="Packets.Game.ClientViolation.DataChecksumMismatch"/> is ignored.
    /// </remarks>
    public interface IVipList : IComponentInterface
    {
        /// <summary>
        /// Gets whether a player is a VIP because of its client, before its name is checked:
        /// Vip:AllVips is set, or Vip:AllowPrerelease is set and the client is a Continuum newer than <paramref name="serverContinuumVersion"/>.
        /// </summary>
        /// <remarks>
        /// This is decided from the login packet alone, and only this decides whether S2C 0x34 echoes the client's own version.
        /// </remarks>
        /// <param name="clientType">The player's client type.</param>
        /// <param name="clientVersion">The client version from the login packet.</param>
        /// <param name="serverContinuumVersion">The Continuum version the server asks clients to run.</param>
        /// <returns><see langword="true"/> if the player is a VIP because of its client.</returns>
        bool IsClientVip(ClientType clientType, ushort clientVersion, ushort serverContinuumVersion);

        /// <summary>
        /// Gets whether a player name is listed in vip.txt (case-insensitive), or vip.txt has the *! wildcard, which lists everyone.
        /// </summary>
        /// <param name="name">The player name.</param>
        /// <returns><see langword="true"/> if the name is listed.</returns>
        bool IsListed(ReadOnlySpan<char> name);
    }
}
