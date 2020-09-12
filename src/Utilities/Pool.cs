using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace SS.Utilities
{
    internal interface IPool
    {
        void Release(PooledObject obj);
    }

    public class Pool<T> : IPool where T : PooledObject, new() 
    {
        private static Pool<T> _default = new Pool<T>();

        public static Pool<T> Default
        {
            get { return _default; }
        }

        private LinkedList<PooledObject> _availableList = new LinkedList<PooledObject>();

        private int _objectsCreated = 0;
        public int ObjectsCreated
        {
            get { return _objectsCreated; }
        }

        public int ObjectsAvailable
        {
            get
            {
                lock (_availableList)
                {
                    return _availableList.Count;
                }
            }
        }

        public Pool()
        {
        }

        /// <summary>
        /// To get an object from the pool.  If the pool has no more available objects, a new one will be created.
        /// Remember to Release the object when done with it so that it will be returned to the pool.
        /// Note: PooledObjects are aware of the pool they originated from, disposing them will return them to the pool they came from.
        /// </summary>
        /// <returns></returns>
        public T Get()
        {
            LinkedListNode<PooledObject> node;

            lock (_availableList)
            {
                node = _availableList.First;

                if (node != null)
                {
                    _availableList.RemoveFirst();
                }
            }

            T obj;
            if (node == null)
            {
                // none available, create one
                obj = new T();
                obj.Pool = this;

                Interlocked.Increment(ref _objectsCreated);
            }
            else
            {
                obj = node.Value as T;
            }

            return obj;
        }

        public void Release(T obj)
        {
            if (obj.Pool != this)
                return; // object did not come from this pool

            LinkedListNode<PooledObject> node = obj.Node;
            lock (_availableList)
            {
                _availableList.AddLast(node);
            }
        }

        #region IPool Members

        void IPool.Release(PooledObject obj)
        {
            T o = obj as T;
            if (o == null)
                return; // object is not of the correct type

            Release(o);
        }

        #endregion
    }
}
