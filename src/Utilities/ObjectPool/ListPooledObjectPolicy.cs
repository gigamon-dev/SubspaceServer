using Microsoft.Extensions.ObjectPool;
using System.Collections.Generic;

#nullable enable

namespace SS.Utilities.ObjectPool
{
    /// <summary>
    /// A policy for pooling <see cref="List{T}"/> instances.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list.</typeparam>
    public class ListPooledObjectPolicy<T> : IPooledObjectPolicy<List<T>>
    {
        /// <summary>
        /// Gets or sets the initial capacity of pooled <see cref="List{T}"/> instances.
        /// </summary>
        /// <value>Defaults to <c>-1</c>, which means use the default initial capacity of <see cref="List{T}"/>.</value>
        public int InitialCapacity { get; init; } = -1;

        /// <summary>
        /// Gets or sets the maximum value for <see cref="List{T}.Capacity"/> that is allowed to be
        /// retained, when <see cref="Return(List{T})"/> is invoked.
        /// </summary>
        /// <value>Defaults to <c>-1</c>, which means do not enforce a limit on capacity.</value>
        public int MaximumRetainedCapacity { get; init; } = -1;

        public List<T> Create()
        {
            return InitialCapacity < 0
                ? new List<T>()
                : new List<T>(InitialCapacity);
        }

        public bool Return(List<T> obj)
        {
            if (obj is null)
                return false;

            if (MaximumRetainedCapacity >= 0 && obj.Capacity > MaximumRetainedCapacity)
            {
                // The capacity is too large. Discard it.
                return false;
            }

            obj.Clear();
            return true;
        }
    }
}
