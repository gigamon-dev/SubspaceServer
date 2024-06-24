using Microsoft.Extensions.ObjectPool;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable

namespace SS.Utilities.ObjectPool
{
	/// <summary>
	/// A policy for pooling of <see cref="LinkedListNode{T}"/> instances.
	/// </summary>
	public class LinkedListNodePooledObjectPolicy<T> : PooledObjectPolicy<LinkedListNode<T>>
	{
		public override LinkedListNode<T> Create()
		{
			return new LinkedListNode<T>(default!);
		}

		public override bool Return(LinkedListNode<T> obj)
		{
			if (obj is null)
				return false;

			Debug.Assert(obj.List is null);

			if (obj.List is not null)
				return false;

			obj.ValueRef = default!;
			return true;
		}
	}
}
