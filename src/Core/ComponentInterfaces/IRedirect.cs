using System;
using System.Net;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that sends <see cref="Packets.Game.S2C_Redirect"/> packets that tell clients to switch to a different zone server.
    /// </summary>
    public interface IRedirect : IComponentInterface
    {
        /// <summary>
        /// Attempts to redirect a <paramref name="target"/> set of players to a configured <paramref name="destination"/>.
        /// </summary>
        /// <param name="target">The target player(s) to redirect.</param>
        /// <param name="destination">The destination to look for.</param>
        /// <returns>True if the <paramref name="target"/> players were sent a redirect. Otherwise, false.</returns>
        bool AliasRedirect(ITarget target, ReadOnlySpan<char> destination);

        /// <summary>
        /// Attempts to redirect a <paramref name="target"/> set of players to a specified zone/arena.
        /// </summary>
        /// <param name="target">The target player(s) to redirect.</param>
        /// <param name="ipEndPoint">The IP and port to redirect to.</param>
        /// <param name="arenaType">The arena type (same as for the ?go packet).</param>
        /// <param name="arenaName">The arena name.</param>
        /// <returns>True if the <paramref name="target"/> players were sent a redirect. Otherwise, false.</returns>
        bool RawRedirect(ITarget target, IPEndPoint ipEndPoint, short arenaType, ReadOnlySpan<char> arenaName);

        /// <summary>
        /// Attempts to redirect a player for an arena change.
        /// </summary>
        /// <param name="player">The player to redirect.</param>
        /// <param name="arenaName">The arena name to look for a redirect on.</param>
        /// <returns>True if a redirect was sent to the <paramref name="player"/>. Otherwise, false.</returns>
        bool ArenaRequest(Player player, ReadOnlySpan<char> arenaName);
    }
}
