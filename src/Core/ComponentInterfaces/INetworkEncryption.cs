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
        /// <param name="data">The buffer to encrypt.  This is guaranteed to be larger than <paramref name="length"/> by at least 3 bytes.</param>
        /// <param name="length">The # of bytes to encrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player player, Span<byte> data, int length);

        /// <summary>
        /// Decrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="player">The player decrypting data for.</param>
        /// <param name="data">The buffer to decrypt.  This is guaranteed to be larger than <paramref name="length"/> by at least 3 bytes.</param>
        /// <param name="length">The # of bytes to decrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player player, Span<byte> data, int length);

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
	/// <param name="remoteAddress">
	/// The <see cref="SocketAddress"/> the request came from.
	/// This object can be used to respond by calling <see cref="INetworkEncryption.ReallyRawSend(SocketAddress, ReadOnlySpan{byte}, ListenData)"/>.
	/// It can also be used to allocate an <see cref="IPEndPoint"/> object 
    /// for calling <see cref="INetworkEncryption.NewConnection(ClientType, IPEndPoint, string, ListenData)"/> 
    /// or to read the IP address and port.
	/// This object should not be stored or held on to. It is mutable and the Network module reuses it for every datagram received.
	/// </param>
	/// <param name="data">The request data.</param>
	/// <param name="listenData">State info to pass to <see cref="INetworkEncryption.NewConnection(ClientType, IPEndPoint, string, ListenData)"/>.</param>
	/// <returns>Whether the request was handled. True means processing is done. False means the request will be given to later handlers to process.</returns>
	public delegate bool ConnectionInitHandler(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData listenData);

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
		/// <param name="remoteAddress">The address to send data to.</param>
		/// <param name="data">The data to send.</param>
		/// <param name="listenData">Information about which socket to send from.</param>
		void ReallyRawSend(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData listenData);

		/// <summary>
		/// Gets a player object for a new connection.
		/// </summary>
		/// <param name="clientType">The type of client the connection is for.</param>
		/// <param name="remoteEndpoint">The endpoint (IP and port) of the connection.</param>
		/// <param name="encryptorName">The encryption interface key.</param>
		/// <param name="listenData">The <see cref="ListenData"/> of the socket that the connection came from.</param>
		/// <returns>The player object for the connection. <see langword="null"/> if there was an error.</returns>
		Player NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string encryptorName, ListenData listenData);
	}
}
