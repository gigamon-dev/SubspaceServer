using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core
{
    public class DataBuffer : PooledObject
    {
        public readonly byte[] Bytes = new byte[Constants.MaxConnInitPacket + 4];

        /// <summary>
        /// # of bytes used in the byte[]
        /// </summary>
        public int NumBytes;

        public DataBuffer()
        {
        }

        public virtual void Clear()
        {
            Array.Clear(Bytes, 0, Bytes.Length);
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
