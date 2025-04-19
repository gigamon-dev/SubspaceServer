using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Represents the status of a connection to a billing server.
    /// </summary>
    public enum BillingStatus
    {
        /// <summary>
        /// Not connected and will not attempt to connect.
        /// </summary>
        Disabled = 0,

        /// <summary>
        /// Not connected, but may attempt to reconnect.
        /// </summary>
        Down = 1,

        /// <summary>
        /// Connected.
        /// </summary>
        Up = 2,
    }

    /// <summary>
    /// Interface that a billing module may implement.
    /// </summary>
    public interface IBilling : IComponentInterface
    {
        /// <summary>
        /// Gets the status of the connection to the billing server.
        /// </summary>
        /// <returns>The status.</returns>
        BillingStatus GetStatus();

        /// <summary>
        /// Gets the identity data, if the billing server provided one.
        /// </summary>
        /// <param name="buffer">A buffer to fill with the identity data. This needs to be large enough to hold all the bytes of an identity. 256 bytes should be sufficient.</param>
        /// <param name="bytesWritten">When this method returns, contains the number of bytes that were written to <paramref name="buffer"/>.</param>
        /// <returns>
        /// <see langword="true"/> if there was an identity and the entirety of its data was written in to <paramref name="buffer"/>.
        /// <see langword="false"/> if there was no identity or <paramref name="buffer"/> was not large enough to store the whole identity.
        /// </returns>
        bool TryGetIdentity(Span<byte> buffer, out int bytesWritten);

        /// <summary>
        /// Gets the user database id of a player.
        /// </summary>
        /// <param name="player">The player to get the id of.</param>
        /// <param name="userId">When this method returns, the id of the player if found. Otherwise, 0.</param>
        /// <returns><see langword="true"/> if the user id could be retrieved. Otherwise, <see langword="false"/>.</returns>
        bool TryGetUserId(Player player, out uint userId);

        /// <summary>
        /// Gets a player's usage.
        /// </summary>
        /// <param name="player">The player to get usage data for.</param>
        /// <param name="usage">The amount of time the player has played. This does not include the current session.</param>
        /// <param name="firstLoginTimestamp">The timestamp of the player's first login.</param>
        /// <returns><see langword="true"/> if usage data could be retrieved. Otherwise, <see langword="false"/>.</returns>
        bool TryGetUsage(Player player, out TimeSpan usage, out DateTime? firstLoginTimestamp);
    }

    /// <summary>
    /// Represents the result of an authentication check.
    /// </summary>
    public enum BillingFallbackResult
    {
        /// <summary>
        /// Player has an entry that matches.
        /// </summary>
        Match,

        /// <summary>
        /// Player has an entry, but wrong password.
        /// </summary>
        Mismatch,

        /// <summary>
        /// Player does not have an entry.
        /// </summary>
        NotFound,
    }

    /// <summary>
    /// Delegate for a callback to indicate the 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="state"></param>
    /// <param name="result"></param>
    public delegate void BillingFallbackDoneDelegate<T>(T state, BillingFallbackResult result);

    /// <summary>
    /// Interface for a service that a billing client module can use to fallback on for assistance with authentication when its connnection to the billing server is down.
    /// With this, the billing client module is still responsible for performing the authentication, it just asks for help in checking a player's name/password combination.
    /// This allows for example, the <see cref="Modules.AuthFile"/> module to be used as a fallback, to allow staff to authenticate when a billing server is down.
    /// </summary>
    public interface IBillingFallback : IComponentInterface
    {
        /// <summary>
        /// Checks whether a player should be considered as being authenticated.
        /// </summary>
        /// <typeparam name="T">Type of state to use for the <paramref name="done"/> callback.</typeparam>
        /// <param name="player">The player to check.</param>
        /// <param name="name">The name the player is attempting to authenticate with.</param>
        /// <param name="password">The password the player is attempting to authenticate with.</param>
        /// <param name="done">A callback to call when a determination has been reached.</param>
        /// <param name="state">The state to send when invoking the callback.</param>
        void Check<T>(Player player, ReadOnlySpan<char> name, ReadOnlySpan<char> password, BillingFallbackDoneDelegate<T> done, T state);
    }
}
