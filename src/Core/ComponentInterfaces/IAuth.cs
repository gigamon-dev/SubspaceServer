using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public delegate void AuthDoneDelegate(Player p, AuthData data);

    public interface IAuth : IComponentInterface
    {
        void Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done);
    }
}
