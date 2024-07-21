using SS.Utilities;
using System;

namespace SS.Core
{
    public class DataBuffer : PooledObject
    {
        public readonly byte[] Bytes;

        /// <summary>
        /// # of bytes used in the byte[]
        /// </summary>
        public int NumBytes;

        public DataBuffer() : this(Constants.MaxPacket + 4)
        {
        }

        public DataBuffer(int capacity)
        {
            Bytes = new byte[capacity];
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
