using System;
using System.Net;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface of a service that handles events for a client connection.
    /// </summary>
    public interface IClientConnectionHandler
    {
        /// <summary>
        /// Processes after the connection has been established (encryption handshake completed).
        /// </summary>
        void Connected();

        /// <summary>
        /// Processes incoming data.
        /// </summary>
        /// <param name="data">The buffer containing the packet data that was received.</param>
        /// <param name="flags">Flags indicating how the data was received.</param>
        void HandlePacket(Span<byte> data, NetReceiveFlags flags);

        /// <summary>
        /// Processes when the connection has been disconnected.
        /// </summary>
        void Disconnected();
    }

    /// <summary>
    /// Interface for a connection that is acting as a client communicating to a server using the Subspace 'core' protocol.
    /// </summary>
    public interface IClientConnection
    {
        IClientConnectionHandler Handler { get; }

        #region Extra Data methods

        bool TryAddExtraData<T>(T value) where T : class;
        bool TryGetExtraData<T>(out T value) where T : class;
        bool TryRemoveExtraData<T>(out T value) where T : class;

        #endregion
    }

    /// <summary>
    /// Interface for a service that can encrypt and decrypt data for a client connection.
    /// </summary>
    public interface IClientEncrypt : IComponentInterface
    {
        /// <summary>
        /// Initializes encryption for a connection.
        /// </summary>
        /// <param name="connection">The client connection to initialize.</param>
        void Initialize(IClientConnection connection);

        /// <summary>
        /// Encrypts data for a connection, in place.
        /// </summary>
        /// <param name="connection">The client connection to encrypt data for.</param>
        /// <param name="data">A buffer containing the data to encrypt.</param>
        /// <param name="length">The # of bytes in <paramref name="data"/> to encrypt.</param>
        /// <returns></returns>
        int Encrypt(IClientConnection connection, Span<byte> data, int length);

        /// <summary>
        /// Decrypts data for a connection, in place.
        /// </summary>
        /// <param name="connection">The client connection to decrypt data for.</param>
        /// <param name="data">A buffer containing the data to decrypt.</param>
        /// <param name="length">The # of bytes in <paramref name="data"/> to decrypt.</param>
        /// <returns></returns>
        int Decrypt(IClientConnection connection, Span<byte> data, int length);

        /// <summary>
        /// Performs cleanup of encryption resources for a connection.
        /// </summary>
        /// <remarks>
        /// This is called when a connection being disconnected.
        /// </remarks>
        /// <param name="connection">The client connection to perform cleanup on.</param>
        void Void(IClientConnection connection);
    }

    /// <summary>
    /// Delegate for a callback when the send of reliable data completes.
    /// </summary>
    /// <param name="connection">The connection the data was being sent to.</param>
    /// <param name="success">Whether the data was sucessfully sent. <see langword="true"/> if an ACK was received. <see langword="false"/> if the send was cancelled out.</param>
    public delegate void ClientReliableCallback(IClientConnection connection, bool success);

    /// <summary>
    /// Interface of a service that provides the ability to act as a client communicating to a server using the Subspace 'core' protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Modules.BillingUdp"> module uses this to act as a client to a billing server.
    /// </para>
    /// <para>
    /// The <see cref="Modules.Network"/> module implements this interface since it contains the logic of communicating using the Subspace 'core' protocol.
    /// </para>
    /// </remarks>
    public interface INetworkClient : IComponentInterface
    {
        /// <summary>
        /// Opens a new client connection to a server.
        /// </summary>
        /// <param name="endPoint">The IP address and port of the server to connect to.</param>
        /// <param name="handler">
        /// The handler for the connection that will handle what to do when it's connected, data is received, or it's disconnected.
        /// <para>All calls to the handler are guaranteed to be executed on the mainloop thread.</para>
        /// </param>
        /// <param name="encryptorName">The name of the encryptor (<see cref="IClientEncrypt"/>) to use for the connection.</param>
        /// <returns>The connection if one was created. <see langword="null"/> if there was a problem.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="endPoint"/> is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="encryptorName"/> is null or white-space.</exception>
        IClientConnection MakeClientConnection(IPEndPoint endPoint, IClientConnectionHandler handler, string encryptorName);

        /// <summary>
        /// Sends data to the server.
        /// </summary>
        /// <param name="connection">The connection to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags indicating how the data should be sent.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        void SendPacket(IClientConnection connection, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to the server.
        /// </summary>
        /// <typeparam name="T">The type of struct containing data to send.</typeparam>
        /// <param name="connection">The connection to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags indicating how the data should be sent.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        void SendPacket<T>(IClientConnection connection, ref T data, NetSendFlags flags) where T : struct;

        /// <summary>
        /// Reliably sends <paramref name="data"/> to a <paramref name="connection"/> and invokes a <paramref name="callback"/> 
        /// after receiving an ACK (successfully sent) or cancelling out (e.g. disconnect, lagout, etc.).
        /// </summary>
        /// <remarks>
        /// The <paramref name="callback"/> is executed on the mainloop thread.
        /// </remarks>
        /// <param name="connection">The connection to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        void SendWithCallback(IClientConnection connection, ReadOnlySpan<byte> data, ClientReliableCallback callback);

        /// <summary>
        /// Initiates the disconnection of a connection.
        /// </summary>
        /// <remarks>
        /// When the disconnection completes, <see cref="IClientConnectionHandler.Disconnected"/> will be invoked.
        /// </remarks>
        /// <param name="connection">The connection to disconnect.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        void DropConnection(IClientConnection connection);

        /// <summary>
        /// Gets statistics about the connection.
        /// </summary>
        /// <param name="connection">The connection to get stats about.</param>
        /// <param name="stats">The object to populate with statistics.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        void GetConnectionStats(IClientConnection connection, ref NetConnectionStats stats);
    }
}
