using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// interface that billing module use to communicate with the network module
    /// </summary>
    public interface INetworkClient : IComponentInterface
    {
        BaseClientConnection MakeClientConnection(string address, int port, IClientConn icc, IClientEncrypt ice);
        void SendPacket(BaseClientConnection cc, byte[] pkt, int len, int flags);
        void DropConnection(BaseClientConnection cc);
    }
}
