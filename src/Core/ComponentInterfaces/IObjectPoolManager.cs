using Microsoft.Extensions.ObjectPool;
using SS.Utilities;
using System.Collections.Generic;
using System.Net;
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
        Pool<T> GetPool<T>() where T : PooledObject, new();

        /// <summary>
        /// Gets the known pools.
        /// </summary>
        IEnumerable<IPool> Pools { get; }

        /// <summary>
        /// Adds a pool to be tracked.
        /// This is useful for monitoring private pools.
        /// </summary>
        /// <param name="pool">The pool to track.</param>
        /// <returns><see langword="true"/> if the pool was added. <see langword="false"/> if it is already being tracked.</returns>
        bool TryAddTracked(IPool pool);

        /// <summary>
        /// Removes a pool from being tracked.
        /// </summary>
        /// <param name="pool">The pool to remove from tracking.</param>
        /// <returns><see langword="true"/> if the pool was removed. Otherwise, <see langword="false"/>.</returns>
        bool TryRemoveTracked(IPool pool);

        // TODO: add methods to free up memory by clearing pools or reducing their size

        //
        // Other pools
        //

        /// <summary>
        /// Pool of <see cref="Player"/> <see cref="HashSet{T}"/>s that can be reused.
        /// </summary>
        /// <remarks>
        /// The set is guaranteed to be empty when it is taken from the pool.
        /// The set is automatically cleared when it is returned to the pool.
        /// </remarks>
        ObjectPool<HashSet<Player>> PlayerSetPool { get; }

        /// <summary>
        /// Pool of <see cref="Arena"/> <see cref="HashSet{T}"/>s that can be reused.
        /// </summary>
        /// <remarks>
        /// The set is guaranteed to be empty when it is taken from the pool.
        /// The set is automatically cleared when it is returned to the pool.
        /// </remarks>
        ObjectPool<HashSet<Arena>> ArenaSetPool { get; }

        /// <summary>
        /// A pool of <see cref="HashSet{T}"/>s for holding case insensitive names.
        /// </summary>
        /// <remarks>
        /// Intended to hold player names or arena names.
        /// For example, certain functionality may operate on player names instead of <see cref="Player"/> objects
        /// when it needs to represent not only players that are currently connected, but also those that might have disconnected.
        /// </remarks>
        ObjectPool<HashSet<string>> NameHashSetPool { get; }

        /// <summary>
        /// Pool of <see cref="StringBuilder"/> objects.
        /// </summary>
        ObjectPool<StringBuilder> StringBuilderPool { get; }

        /// <summary>
        /// Pool of <see cref="IPEndPoint"/> objects.
        /// </summary>
        ObjectPool<IPEndPoint> IPEndPointPool { get; }
    }
}
