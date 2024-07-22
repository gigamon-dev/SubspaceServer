using System;
using System.Net;

namespace SS.Core.ComponentInterfaces
{

    /// <summary>
    /// Delegate for a handler to a connection init request.
    /// </summary>
    /// <param name="remoteAddress">
    /// The <see cref="SocketAddress"/> the request came from.
    /// This object can be used to respond by calling <see cref="IRawNetwork.ReallyRawSend(SocketAddress, ReadOnlySpan{byte}, ListenData)"/>.
    /// It can also be used to allocate an <see cref="IPEndPoint"/> object 
    /// for calling <see cref="IRawNetwork.NewConnection(ClientType, IPEndPoint, string, ListenData)"/> 
    /// or to read the IP address and port.
    /// This object should not be stored or held on to. It is mutable and the Network module reuses it for every datagram received.
    /// </param>
    /// <param name="data">The request data.</param>
    /// <param name="listenData">State info to pass to <see cref="IRawNetwork.NewConnection(ClientType, IPEndPoint, string, ListenData)"/>.</param>
    /// <returns>Whether the request was handled. True means processing is done. False means the request will be given to later handlers to process.</returns>
    public delegate bool ConnectionInitHandler(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData listenData);

    /// <summary>
    /// Delegate for a handler to an incoming peer packet.
    /// </summary>
    /// <param name="remoteAddress">The address the packet came from.</param>
    /// <param name="data">The data of the packet.</param>
    /// <returns>
    /// <see langword="true"/> if the packet was successfully handled.
    /// <see langword="false"/> if the packet was ignored.
    /// </returns>
    public delegate bool PeerPacketHandler(SocketAddress remoteAddress, ReadOnlySpan<byte> data);

    /// <summary>
    /// Interface that provides special methods to the network module.
    /// </summary>
    /// <remarks>
    /// In most cases, the <see cref="INetwork"/> interface is what should be used.
    /// This interface is for when low-level UDP network functionality is required:
    /// mainly the encryption modules which handle connection init packets, 
    /// and the peer module which handles peer packets.
    /// </remarks>
    public interface IRawNetwork : IComponentInterface
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
        /// Registers a handler for peer packets.
        /// </summary>
        /// <param name="handler">The handler to register.</param>
        /// <returns><see langword="true"/> if the handler was registered. Otherwise, <see langword="false"/>.</returns>
        bool RegisterPeerPacketHandler(PeerPacketHandler handler);

        /// <summary>
        /// Unregisters a previously registered handler for peer packets.
        /// </summary>
        /// <param name="handler">The handler to unregister</param>
        /// <returns><see langword="true"/> if the handler was unregistered. Otherwise, <see langword="false"/>.</returns>
        bool UnregisterPeerPacketHandler(PeerPacketHandler handler);

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
