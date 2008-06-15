using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core
{
    public class DataBuffer : PooledObject
    {
        public readonly byte[] Bytes = new byte[Constants.MaxPacket + 4]; // asss does MAXPACKET+4 and i'm not sure why
        public int NumBytes;

        public DataBuffer()
        {
        }

        public void Clear()
        {
            for (int x = 0; x < Bytes.Length; x++)
            {
                Bytes[x] = 0;
            }

            NumBytes = 0;
        }

        protected override void Dispose(bool isDisposing)
        {
            Clear();

            base.Dispose(isDisposing); // returns this object to its pool
        }
    }
}
