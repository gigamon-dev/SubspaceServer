using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public class AuthData
    {
        public bool demodata;
        public AuthCode code;
        public bool authenticated;
        public string name;
        public string sendname;
        public string squad;
        public string customtext;
    }

    public delegate void AuthDoneDelegate(Player p, AuthData data);

    public interface IAuth : IComponentInterface
    {
        void Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done);
    }
}
