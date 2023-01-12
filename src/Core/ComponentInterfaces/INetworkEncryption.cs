using System;
using System.Net;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that can encrypt and decrypt data for players.
    /// </summary>
    public interface IEncrypt : IComponentInterface
    {
        /// <summary>
        /// Encrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="player">The player encrypting data for.</param>
        /// <param name="data">The buffer to encrypt.  This is guaranteed to be larger than <paramref name="len"/> by at least 4 bytes.</param>
        /// <param name="len">The # of bytes to encrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player player, Span<byte> data, int len);

        /// <summary>
        /// Decrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="player">The player decrypting data for.</param>
        /// <param name="data">The buffer to decrypt.  This is guaranteed to be larger than <paramref name="len"/> by at least 4 bytes.</param>
        /// <param name="len">The # of bytes to decrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player player, Span<byte> data, int len);

        /// <summary>
        /// Called when encryption info for a player is no longer needed (e.g. the player disconnects).
        /// This allows for cleanup to be done, if needed.
        /// </summary>
        /// <param name="player">The player to cleanup for.</param>
        void Void(Player player);
    }

    /// <summary>
    /// Delegate for a handler to a connection init request.
    /// </summary>
    /// <param name="remoteEndpoint">Endpoint the request came from.</param>
    /// <param name="buffer">The request data.</param>
    /// <param name="len">The length of the data.</param>
    /// <param name="ld">State info to pass to <see cref="INetworkEncryption.NewConnection(ClientType, IPEndPoint, string, ListenData)"/>.</param>
    /// <returns>Whether the request was handled. True means processing is done. False means the request will be given to later handlers to process.</returns>
    public delegate bool ConnectionInitHandler(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld);

    /// <summary>
    /// Interface with special methods for encryption modules to use to access the network module.
    /// </summary>
    public interface INetworkEncryption : IComponentInterface
    {
        /// <summary>
        /// Adds a handler to the end of the connection init pipeline.
        /// </summary>
        /// <remarks>The <paramref name="handler"/> is called on Network module's ReceiveThread."/></remarks>
        /// <param name="handler">The handler to add.</param>
        void AppendConnectionInitHandler(ConnectionInitHandler handler);

        /// <summary>
        /// Removes a handler from the connection init pipeline.
        /// </summary>
        /// <param name="handler">The handler to remove.</param>
        /// <returns>True if the handler was removed. Otherwise, false.</returns>
        bool RemoveConnectionInitHandler(ConnectionInitHandler handler);

        /// <summary>
        /// Sends data immediately without encryption and without buffering.
        /// </summary>
        /// <param name="remoteEndpoint"></param>
        /// <param name="data"></param>
        /// <param name="ld"></param>
        void ReallyRawSend(IPEndPoint remoteEndpoint, ReadOnlySpan<byte> data, ListenData ld);

        /// <summary>
        /// Gets a player object for a new connection.
        /// </summary>
        /// <param name="clientType"></param>
        /// <param name="remoteEndpoint"></param>
        /// <param name="iEncryptName"></param>
        /// <param name="ld"></param>
        /// <returns></returns>
        Player NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string iEncryptName, ListenData ld);
    }
}
