using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
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
