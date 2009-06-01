using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core
{
    public class DataBuffer : PooledObject
    {
        /// <summary>
        /// asss does MAXPACKET+4 because:
        /// The idea was that if you get a packet that's the maximum size, and have a pointer to the very last byte, you should still be able to read 4 bytes starting at that address. That is, 3 past the end of the packet. It only has to be +3, but if you're doing +3 you might as well do +4, because they're the same due to alignment. This simplifies some packet-handling code, at least in theory.
        /// </summary>
        public readonly byte[] Bytes = new byte[Constants.MaxPacket + 4];

        /// <summary>
        /// # of bytes used in the byte[]
        /// </summary>
        public int NumBytes;

        public DataBuffer()
        {
        }

        public virtual void Clear()
        {
            for (int x = 0; x < Bytes.Length; x++)
            {
                Bytes[x] = 0;
            }

            NumBytes = 0;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                Clear();

            base.Dispose(isDisposing); // returns this object to its pool
        }
    }
}
