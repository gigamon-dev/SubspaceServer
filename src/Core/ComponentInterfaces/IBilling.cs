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
        /// Gets identity information about the billing server, if the server provided it.
        /// </summary>
        /// <returns>The identity data, or <see cref="ReadOnlySpan{byte}.Empty"/> is no identity data was provided.</returns>
        ReadOnlySpan<byte> GetIdentity();

        /// <summary>
        /// Gets the user database id of a player.
        /// </summary>
        /// <param name="player">The player to get the id of.</param>
        /// <param name="userId">When this method returns, the id of the player if found. Otherwise, 0.</param>
        /// <returns><see langword="true"/> if the user id could be retrieved. Otherwise, <see langword="false"/></returns>
        bool TryGetUserId(Player player, out uint userId);
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
