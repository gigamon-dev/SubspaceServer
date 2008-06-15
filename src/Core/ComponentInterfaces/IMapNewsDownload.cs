using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IMapNewsDownload : IComponentInterface
    {
        void SendMapFilename(Player p);
        uint GetNewsChecksum();
    }
}
