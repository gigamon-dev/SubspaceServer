using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core
{
    public class DataBuffer : PooledObject
    {
        public readonly byte[] Bytes = new byte[Constants.MaxPacket + 4];
        public int NumBytes;

        public DataBuffer()
        {
        }
    }
}
