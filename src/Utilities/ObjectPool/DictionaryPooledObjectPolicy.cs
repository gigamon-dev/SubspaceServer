using Microsoft.Extensions.ObjectPool;
using System.Collections.Generic;

#nullable enable

namespace SS.Utilities.ObjectPool
{
	/// <summary>
	/// A policy for pooling <see cref="Dictionary{TKey, TValue}"/> instances.
	/// </summary>
	/// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
	/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
	public class DictionaryPooledObjectPolicy<TKey, TValue> : IPooledObjectPolicy<Dictionary<TKey, TValue>> where TKey : notnull
	{
		/// <summary>
		/// Gets or sets the initial capacity of pooled <see cref="Dictionary{TKey, TValue}"/> instances.
		/// </summary>
		/// <value>Defaults to -1, which means use the default initial capacity of <see cref="Dictionary{TKey, TValue}"/>.</value>
		public int InitialCapacity { get; init; } = -1;

		/// <summary>
		/// Gets or sets the equality comparer of pooled <see cref="Dictionary{TKey, TValue}"/> instances.
		/// </summary>
		/// <value>Defaults to <c>null</c>, which means use the default <see cref="IEqualityComparer{T}"/> for the type of the key.</value>
		public IEqualityComparer<TKey>? EqualityComparer { get; init; } = null;

		public Dictionary<TKey, TValue> Create()
		{
			return InitialCapacity < 0
				? new Dictionary<TKey, TValue>(EqualityComparer)
				: new Dictionary<TKey, TValue>(InitialCapacity, EqualityComparer);
		}

		public bool Return(Dictionary<TKey, TValue> obj)
		{
			if (obj is null)
				return false;

			obj.Clear();
			return true;
		}
	}
}
