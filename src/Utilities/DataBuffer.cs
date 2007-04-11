using System;

namespace SS.Utilities
{
    /// <summary>
    /// Pooled buffer used to derive off of for use in the BufferPool class.
    /// </summary>
    /// <typeparam name="T">the derived class</typeparam>
    public abstract class DataBuffer<T> : IDisposable
        where T : DataBuffer<T>, new() 
    {
        internal BufferPool<T> _pool = null;
        private T thisAsSubclass;

        public DataBuffer()
        {
            thisAsSubclass = this as T;

            if (thisAsSubclass == null)
                throw new Exception("derived classes must send themselves as the template type parameter");
        }

        /// <summary>
        /// If the buffer was created by a pool, returns it back to the pool for future use.
        /// Otherwise, no action is taken.
        /// </summary>
        public void Release()
        {
            if (_pool != null)
            {
                Clear();
                _pool.ReleaseBuffer(thisAsSubclass);
            }
        }

        /// <summary>
        /// Can be overriden in derived classes to clear data members before the the buffer is returned to a pool.
        /// Default implementation does nothing.
        /// </summary>
        public virtual void Clear()
        {
            // no-op
        }

        #region IDisposable Members

        public void Dispose()
        {
            Release();
        }

        #endregion
    }
}
