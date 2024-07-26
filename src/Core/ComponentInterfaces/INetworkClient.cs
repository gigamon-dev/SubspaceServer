using System;
using System.Collections.Concurrent;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Represents a connection that is acting as a client communicating to a server using the Subspace 'core' protocol.
    /// </summary>
    public abstract class ClientConnection(IClientConnectionHandler handler)
    {
        /// <summary>
        /// The handler for the client connection.
        /// </summary>
        public IClientConnectionHandler Handler { get; } = handler ?? throw new ArgumentNullException(nameof(handler));

        #region Extra Data

        private readonly ConcurrentDictionary<Type, object> _extraData = new();

        public bool TryAddExtraData<T>(T value) where T : class
        {
            return _extraData.TryAdd(typeof(T), value);
        }

        public bool TryGetExtraData<T>(out T value) where T : class
        {
            if (_extraData.TryGetValue(typeof(T), out object obj))
            {
                value = obj as T;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

        public bool TryRemoveExtraData<T>(out T value) where T : class
        {
            if (_extraData.TryRemove(typeof(T), out object obj))
            {
                value = obj as T;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

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
        /// <param name="cc">The client connection to initialize.</param>
        void Initialze(ClientConnection cc);

        /// <summary>
        /// Encrypts data for a connection, in place.
        /// </summary>
        /// <param name="cc">The client connection to encrypt data for.</param>
        /// <param name="data">A buffer containing the data to encrypt.</param>
        /// <param name="length">The # of bytes in <paramref name="data"/> to encrypt.</param>
        /// <returns></returns>
        int Encrypt(ClientConnection cc, Span<byte> data, int length);

        /// <summary>
        /// Decrypts data for a connection, in place.
        /// </summary>
        /// <param name="cc">The client connection to decrypt data for.</param>
        /// <param name="data">A buffer containing the data to decrypt.</param>
        /// <param name="length">The # of bytes in <paramref name="data"/> to decrypt.</param>
        /// <returns></returns>
        int Decrypt(ClientConnection cc, Span<byte> data, int length);

        /// <summary>
        /// Performs cleanup of encryption resources for a connection.
        /// </summary>
        /// <remarks>
        /// This is called when a connection being disconnected.
        /// </remarks>
        /// <param name="cc">The client connection to perform cleanup on.</param>
        void Void(ClientConnection cc);
    }

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
    /// Delegate for a callback when the send of reliable data completes.
    /// </summary>
    /// <param name="connection">The connection the data was being sent to.</param>
    /// <param name="success">Whether the data was sucessfully sent. <see langword="true"/> if an ACK was received. <see langword="false"/> if the send was cancelled out.</param>
    public delegate void ClientReliableCallback(ClientConnection connection, bool success);

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
        /// <param name="address">The IP address of the server to connect to.</param>
        /// <param name="port">The port of the server to connect to.</param>
        /// <param name="handler">The handler for the connection that will handle what to do when it's connected, data is received, or it's disconnected.</param>
        /// <param name="encryptorName">The name of the encryptor (<see cref="IClientEncrypt"/>) to use for the connection.</param>
        /// <returns>The connection.</returns>
        ClientConnection MakeClientConnection(string address, int port, IClientConnectionHandler handler, string encryptorName);

        /// <summary>
        /// Sends data to the server.
        /// </summary>
        /// <param name="connection">The connection from <see cref="MakeClientConnection"/></param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags indicating how the data should be sent.</param>
        void SendPacket(ClientConnection connection, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to the server.
        /// </summary>
        /// <typeparam name="T">The type of struct containing data to send.</typeparam>
        /// <param name="connection">The connection from <see cref="MakeClientConnection"/></param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags indicating how the data should be sent.</param>
        void SendPacket<T>(ClientConnection connection, ref T data, NetSendFlags flags) where T : struct;

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
        void SendWithCallback(ClientConnection connection, ReadOnlySpan<byte> data, ClientReliableCallback callback);

        /// <summary>
        /// Initiates the disconnection of a connection.
        /// </summary>
        /// <remarks>
        /// When the disconnection completes, <see cref="IClientConnectionHandler.Disconnected"/> will be invoked.
        /// </remarks>
        /// <param name="connection">The connection from <see cref="MakeClientConnection"/></param>
        void DropConnection(ClientConnection connection);

        /// <summary>
        /// Gets statistics about the connection.
        /// </summary>
        /// <param name="connection">The connection from <see cref="MakeClientConnection"/></param>
        /// <param name="stats">The object to populate with statistics.</param>
        void GetConnectionStats(ClientConnection connection, ref NetConnectionStats stats);
    }
}
