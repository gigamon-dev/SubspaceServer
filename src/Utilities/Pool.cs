using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SS.Utilities
{
    public interface IPool
    {
        /// <summary>
        /// The type of <see cref="PooledObject"/> that the pool manages.
        /// </summary>
        Type Type { get; }

        /// <summary>
        /// The total # of objects that have been created by the pool.
        /// </summary>
        int ObjectsCreated { get; }

        /// <summary>
        /// The number of objects in the pool that are available to be taken.
        /// </summary>
        int ObjectsAvailable { get; }
    }

    public interface IPool<T> : IPool where T : PooledObject, new()
    {
        /// <summary>
        /// Gets an object from the pool. If the pool has no more available objects, a new one will be created.
        /// When done with the object, remember to return it to the pool by disposing it.
        /// </summary>
        /// <returns>The object.</returns>
        T Get();
    }

    /// <summary>
    /// Interface for <see cref="PooledObject"/> to be able to return themselves to their originating pool.
    /// </summary>
    internal interface IPoolInternal
    {
        /// <summary>
        /// Returns an object to the pool.
        /// </summary>
        /// <param name="obj">The object to release.</param>
        void Release(PooledObject obj);
    }

    /// <summary>
    /// An object pool implementation where the objects are aware of the pool they originated from and can return themselves to it when disposed.
    /// </summary>
    /// <typeparam name="T">The type of object to store in the pool.</typeparam>
    public class Pool<T> : IPool<T>, IPoolInternal where T : PooledObject, new()
    {
        /// <summary>
        /// The default pool. This can be used like a singleton, recommended.
        /// </summary>
        public static Pool<T> Default { get; set; } = new();

        private readonly ConcurrentBag<T> _availableBag = [];

        public Type Type => typeof(T);

        private int _objectsCreated = 0;

        public int ObjectsCreated => _objectsCreated;

        public int ObjectsAvailable => _availableBag.Count;

        /// <summary>
        /// Initializes a new pool.
        /// It is recommended to use <see cref="Default"/>, but this can be used if a separate pool is needed.
        /// </summary>
        public Pool()
        {
        }

        public T Get()
        {
            if (!_availableBag.TryTake(out T? obj) || !obj.TrySetAvailability(false))
            {
                // none available, create one
                obj = new T()
                {
                    Pool = this,
                };

                Interlocked.Increment(ref _objectsCreated);
            }

            return obj;
        }

        private void Release(T obj)
        {
            if (obj.Pool != this)
            {
                return; // object did not come from this pool
            }

            if (obj.TrySetAvailability(true))
            {
                _availableBag.Add(obj);
            }
        }

        #region IPool Members

        void IPoolInternal.Release(PooledObject obj)
        {
            if (obj is not T o)
                return; // object is not of the correct type

            Release(o);
        }

        #endregion
    }
}
