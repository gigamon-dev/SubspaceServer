using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public class PooledObject : IDisposable
    {
        internal readonly LinkedListNode<PooledObject> Node;
        internal IPool Pool;

        public PooledObject()
        {
            Node = new LinkedListNode<PooledObject>(this);
        }

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (Pool != null)
                    Pool.Release(this); // return this object to the pool it originated from
            }
        }
    }
}
