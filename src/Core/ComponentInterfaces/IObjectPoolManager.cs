using Microsoft.Extensions.ObjectPool;
using SS.Utilities;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a module that manages and tracks object pool usage.
    /// </summary>
    public interface IObjectPoolManager : IComponentInterface
    {
        /// <summary>
        /// Gets the default pool for a given type and begins tracking the pool if it isn't already being tracked.
        /// </summary>
        /// <typeparam name="T">The type of pooled object.</typeparam>
        /// <returns>The pool.</returns>
        IPool<T> GetPool<T>() where T: PooledObject, new();

        /// <summary>
        /// Gets an object from the default pool for its type and begins tracking the pool if it isn't already being tracked.
        /// </summary>
        /// <typeparam name="T">The type of pooled object.</typeparam>
        /// <returns>The object.</returns>
        T Get<T>() where T : PooledObject, new();

        /// <summary>
        /// Gets the known pools.
        /// </summary>
        IEnumerable<IPool> Pools { get; }

        // TODO: add methods to free up memory by clearing pools or reducing their size

        //
        // Other pools
        //

        /// <summary>
        /// Pool of <see cref="Player"/> HashSets that can be reused.
        /// The set is guaranteed to be empty when it is taken from the pool.
        /// The set is automatically cleared when it is returned to the pool.
        /// </summary>
        ObjectPool<HashSet<Player>> PlayerSetPool { get; }

        /// <summary>
        /// Pool of <see cref="StringBuilder"/> objects.
        /// </summary>
        ObjectPool<StringBuilder> StringBuilderPool { get; }
    }
}
