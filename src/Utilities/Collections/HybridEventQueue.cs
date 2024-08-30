using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SS.Utilities.Collections
{
    /// <summary>
    /// A thread-safe producer-consumer queue that includes an <see cref="AutoResetEvent"/> that indicates when there may be an item ready to be dequeued.
    /// An item can only be in the queue once. Attempting to queue an item multiple times has no effect; the item retains its original position.
    /// </summary>
    /// <remarks>
    /// The queue is implemented with a combination of a <see cref="LinkedList{T}"/> and a <see cref="Dictionary{TKey, TValue}"/>.
    /// The linked list is the actual queue. 
    /// The dictionary is used to tell if an item is already in the queue and also holds the <see cref="LinkedListNode{T}"/> to attain an O(1) removal from the queue.
    /// </remarks>
    /// <typeparam name="T">The type of the items contained in the queue.</typeparam>
    /// <param name="initialCapacity">The initial capacity of the queue.</param>
    /// <param name="comparer">The <see cref="IEqualityComparer{T}"/> to use when comparing items, or <see langword="null"/> to use the default <see cref="EqualityComparer{T}"/> of <typeparamref name="T"/>.</param>
    /// <param name="nodePool">An object pool of <see cref="LinkedListNode{T}"/>.</param>
    public sealed class HybridEventQueue<T>(int initialCapacity, IEqualityComparer<T>? comparer, ObjectPool<LinkedListNode<T>> nodePool) : IDisposable where T : notnull
    {
        private readonly object _lock = new();
        private readonly Dictionary<T, LinkedListNode<T>> _itemNodeDictionary = new(initialCapacity, comparer);
        private readonly LinkedList<T> _queue = new();
        private readonly AutoResetEvent _readyEvent = new(false);
        private readonly ObjectPool<LinkedListNode<T>> _nodePool = nodePool ?? throw new ArgumentNullException(nameof(nodePool));
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="HybridEventQueue{T}"/> class that has a specified capacity, using the default equality comparer of <typeparamref name="T"/>.
        /// </summary>
        /// <param name="initialCapacity">The initial capacity of the queue.</param>
        /// <param name="nodePool">An object pool of <see cref="LinkedListNode{T}"/>.</param>
        public HybridEventQueue(int initialCapacity, ObjectPool<LinkedListNode<T>> nodePool)
            : this(initialCapacity, null, nodePool)
        {
        }

        /// <summary>
        /// A wait handle that signals when there may be an item ready to be dequeued.
        /// </summary>
        public AutoResetEvent ReadyEvent => _readyEvent;

        /// <summary>
        /// Tries to add an item to the queue.
        /// </summary>
        /// <remarks>
        /// An item can only be in the queue once. Attempting to queue an item multiple times has no effect; the item retains its original position.
        /// <para>
        /// O(1) as long as the Dictionary doesn't have to resize, which should be the case if a proper initial capacity was provided.
        /// Otherwise O(n) if the Dictionary had to resize.
        /// </para>
        /// </remarks>
        /// <param name="item">The item to add ot the queue.</param>
        /// <returns><see langword="true"/> if the item was added to the queue. <see langword="false"/> if the item was already in the queue.</returns>
        public bool TryEnqueue(T item)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            bool queued = false;
            LinkedListNode<T> node = _nodePool.Get();
            node.Value = item;

            lock (_lock)
            {
                if (queued = _itemNodeDictionary.TryAdd(item, node))
                {
                    _queue.AddLast(node);
                }
            }

            if (queued)
            {
                // This is purposely done outside of the lock, to prevent blocking another thread waiting on the event.
                _readyEvent.Set();
            }
            else
            {
                _nodePool.Return(node);
            }

            return queued;
        }


        /// <summary>
        /// Tries to take the first available item from the queue.
        /// </summary>
        /// <remarks>O(1)</remarks>
        /// <param name="item">When this method returns, the item if one was taken.</param>
        /// <returns><see langword="true"/> if an item was taken from the queue. Otherwise, <see langword="false"/>.</returns>
        public bool TryDequeue([MaybeNullWhen(false)] out T item)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            bool setEvent = false;
            LinkedListNode<T>? node = null;

            try
            {
                lock (_lock)
                {
                    node = _queue.First;
                    if (node is null)
                    {
                        // The queue is empty.
                        item = default;
                        return false;
                    }

                    // Got an item.
                    item = node.Value;
                    _queue.Remove(node);
                    _itemNodeDictionary.Remove(item);
                    setEvent = _queue.Count > 0;
                    return true;
                }
            }
            finally
            {
                if (setEvent)
                {
                    // This is purposely done outside of the lock, to prevent blocking another thread waiting on the event.
                    _readyEvent.Set();
                }

                if (node is not null)
                {
                    _nodePool.Return(node);
                }
            }
        }

        /// <summary>
        /// Tries to remove an item from the queue.
        /// </summary>
        /// <remarks>O(1)</remarks>
        /// <param name="item">The item to remove.</param>
        /// <returns><see langword="true"/> if the item was removed from the queue. Otherwise, <see langword="false"/>.</returns>
        public bool Remove(T item)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            LinkedListNode<T>? node;
            bool removed;

            lock (_lock)
            {
                if (removed = _itemNodeDictionary.Remove(item, out node))
                {
                    _queue.Remove(node!);
                }
            }

            if (node is not null)
            {
                _nodePool.Return(node);
            }

            return removed;
        }

        /// <summary>
        /// Removes all items from the queue.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();

                foreach (LinkedListNode<T> node in _itemNodeDictionary.Values)
                {
                    _nodePool.Return(node);
                }

                _itemNodeDictionary.Clear();

                _readyEvent.Reset();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _readyEvent.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
