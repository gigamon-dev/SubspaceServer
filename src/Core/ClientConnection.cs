using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
    // looks like asss uses this one like a void* type
    public interface IClientEncrypt
    {
    }

    public interface IClientConn
    {
        void Connected();
        void HandlePacket(byte[] pkt, int len);
        void Disconnected();
    }

    public abstract class BaseClientConnection
    {
        protected BaseClientConnection(IClientConn i, IClientEncrypt enc)
        {
            this.i = i;
            this.enc = enc;
        }

        public IClientConn i;
        public IClientEncrypt enc;
        //ClientEncryptData ced;
    }
}
