using Microsoft.Extensions.ObjectPool;
using System.Collections.Generic;

#nullable enable

namespace SS.Utilities.ObjectPool
{
	/// <summary>
	/// A policy for pooling <see cref="HashSet{T}"/> instances.
	/// </summary>
	/// <typeparam name="T">The type of elements in the hash set.</typeparam>
	public class HashSetPooledObjectPolicy<T> : PooledObjectPolicy<HashSet<T>>
	{
		/// <summary>
		/// Gets or sets the initial capacity of pooled <see cref="HashSet{T}"/> instances.
		/// </summary>
		/// <value>Defaults to <c>-1</c>, which means use the default initial capacity of <see cref="HashSet{T}"/>.</value>
		public int InitialCapacity { get; init; } = -1;

		/// <summary>
		/// Gets or sets the equality comparer of pooled <see cref="HashSet{T}"/> instances.
		/// </summary>
		/// <value>Defaults to <c>null</c>, which means use the default <see cref="IEqualityComparer{T}"/> for the hash set type.</value>
		public IEqualityComparer<T>? EqualityComparer { get; init; } = null;

		public override HashSet<T> Create()
		{
			return InitialCapacity < 0
				? new HashSet<T>(EqualityComparer)
				: new HashSet<T>(InitialCapacity, EqualityComparer);
		}

		public override bool Return(HashSet<T> obj)
		{
			if (obj is null)
				return false;

			obj.Clear();
			return true;
		}
	}
}
