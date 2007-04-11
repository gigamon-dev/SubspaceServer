using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Utilities
{
    public class BufferPool<T> where T : DataBuffer<T>, new()
    {
        private LinkedList<T> _bufferList = new LinkedList<T>();
        private LinkedList<T> _nodeList = new LinkedList<T>();

        private int _buffersCreated = 0;

        public BufferPool()
        {
        }

        public int BuffersCreated
        {
            get { return _buffersCreated; }
        }

        public int AvailableBuffers
        {
            get
            {
                lock (_bufferList)
                {
                    return _bufferList.Count;
                }
            }
        }

        /// <summary>
        /// To get a buffer.  If the pool has available buffers, one is taken from it.  Otherwise a new DataBuffer is created.
        /// When done with the DataBuffer, remember to release it back to the pool.
        /// </summary>
        /// <returns></returns>
        public T GetBuffer()
        {
            LinkedListNode<T> node = null;

            lock(_bufferList)
            {
                node = _bufferList.First;

                if (node != null)
                {
                    _bufferList.RemoveFirst();
                }
            }

            if (node == null)
            {
                // no buffers available, create one
                T buffer = new T();
                buffer._pool = this;

                Interlocked.Increment(ref _buffersCreated);

                return buffer;
            }

            try
            {
                return node.Value;
            }
            finally
            {
                node.Value = null;

                lock (_nodeList)
                {
                    _nodeList.AddFirst(node);
                }
            }
        }

        /// <summary>
        /// To add a buffer into the pool.
        /// This method is used by DataBuffers to return themselves back to the pool they were created from.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="node"></param>
        public void ReleaseBuffer(T buffer)
        {
            if (buffer._pool != this)
                throw new InvalidOperationException("only buffers created by this pool can be released to back it");

            LinkedListNode<T> node = null;

            lock (_nodeList)
            {
                node = _nodeList.First;

                if (node != null)
                {
                    _nodeList.RemoveFirst();
                }
            }
            
            if (node == null)
            {
                node = new LinkedListNode<T>(buffer);
            }
            else
            {
                node.Value = buffer;
            }

            lock (_bufferList)
            {
                _bufferList.AddFirst(node);
            }
        }
    }
}
