using System;
using System.Collections.Concurrent;
using System.Net;

namespace SS.Core.ComponentInterfaces
{
    public abstract class ClientConnection
    {
        protected ClientConnection(IClientConnectionHandler handler, IClientEncrypt encryptor)
        {
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            Encryptor = encryptor; // null means no encryption
        }

        public IClientConnectionHandler Handler { get; private set; }
        public IClientEncrypt Encryptor { get; private set; }
        public abstract EndPoint ServerEndpoint { get; }

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
        void Initialze(ClientConnection cc);
        int Encrypt(ClientConnection cc, Span<byte> data, int len);
        int Decrypt(ClientConnection cc, Span<byte> data, int len);
        void Void(ClientConnection cc);
    }

    /// <summary>
    /// Interface of a service that handles events for a client connection.
    /// </summary>
    public interface IClientConnectionHandler
    {
        void Connected();
        void HandlePacket(byte[] pkt, int len);
        void Disconnected();
    }

    /// <summary>
    /// Interface for performing client connections using the subspace 'core' protocol.
    /// </summary>
    /// <remarks>
    /// The billing module use to communicate with the network module
    /// </remarks>
    public interface INetworkClient : IComponentInterface
    {
        ClientConnection MakeClientConnection(string address, int port, IClientConnectionHandler handler, IClientEncrypt encryptor);
        void SendPacket(ClientConnection cc, ReadOnlySpan<byte> data, NetSendFlags flags);
        void DropConnection(ClientConnection cc);
    }
}
