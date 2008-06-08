using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;

namespace SS.Core.ComponentInterfaces
{
    public interface IEncrypt
    {
        /// <summary>
        /// data is encrypted in place
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        /// <param name="len"></param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player p, byte[] data, int len);

        int Encrypt(Player p, ArraySegment<byte> data);

        /// <summary>
        /// data is decrypted in place
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player p, byte[] data, int len);

        /// <summary>
        /// called when the player disconnects
        /// </summary>
        /// <param name="p"></param>
        void Void(Player p);
    }

    /// <summary>
    /// interface that encryption modules use to commuicate with the network module
    /// </summary>
    public interface INetworkEncryption : IComponentInterface
    {
        void ReallyRawSend(IPEndPoint remoteEndpoint, byte[] pkt, int len, object v);
        Player NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, IEncrypt enc, object v);
    }
}
