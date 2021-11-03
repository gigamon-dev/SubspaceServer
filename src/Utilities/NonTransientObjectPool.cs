using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SS.Utilities
{
    /// <summary>
    /// An <see cref="ObjectPool{T}"/> implementation for objects that may be rented for extended periods of time before being returned to the pool.
    /// </summary>
    /// <remarks>
    /// The <see cref="DefaultObjectPool{T}"/> is good for objects which are rented for a brief period and quickly returned to the pool.
    /// Under the hood, it uses a combination of field + array accessed via <see cref="Interlocked"/> methods.
    /// It is designed to retain a small # of objects, based on processor count.
    /// However, there are use cases that fall outside of this realm. For example, objects that may be held up in producer/consumer queues.
    /// The goal of this pool implementation is to provide a quick alternative to support such use cases.
    /// </remarks>
    /// <typeparam name="T">The type object to pool.</typeparam>
    public class NonTransientObjectPool<T> : ObjectPool<T>, IPool where T : class
    {
        private protected readonly ConcurrentBag<T> _items = new();
        private protected readonly IPooledObjectPolicy<T> _policy;
        private protected readonly PooledObjectPolicy<T> _fastPolicy; // to avoid the interface call were possible
        private protected readonly bool _isDefaultPolicy;

        /// <summary>
        /// Initializes a new instance of <see cref="NonTransientObjectPool{T}"/>.
        /// </summary>
        /// <param name="policy">The pooling policy to use.</param>
        public NonTransientObjectPool(IPooledObjectPolicy<T> policy)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _fastPolicy = policy as PooledObjectPolicy<T>;
            _isDefaultPolicy = IsDefaultPolicy();

            bool IsDefaultPolicy()
            {
                var type = policy.GetType();
                return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DefaultPooledObjectPolicy<>);
            }
        }

        public Type Type => typeof(T);

        private int _objectsCreated = 0;
        public int ObjectsCreated => _objectsCreated;

        public int ObjectsAvailable => _items.Count;

        public override T Get()
        {
            if (_items.TryTake(out T item))
                return item;

            Interlocked.Increment(ref _objectsCreated);
            return _fastPolicy?.Create() ?? _policy.Create();
        }

        public override void Return(T obj)
        {
            if (_isDefaultPolicy || (_fastPolicy?.Return(obj) ?? _policy.Return(obj)))
            {
                _items.Add(obj);
            }
        }
    }
}
