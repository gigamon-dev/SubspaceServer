using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.Utilities
{
    /// <summary>
    /// A simple thread safe queue.
    /// Similar to asss' MPQueue.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class MessagePassingQueue<T>
    {
        private object _lockObj = new object();
        private Queue<T> _queue; // TODO: maybe change this to a linked list
        private AutoResetEvent _queuedItemSignal = new AutoResetEvent(false);

        public MessagePassingQueue()
        {
            _queue = new Queue<T>();
        }

        public MessagePassingQueue(IEnumerable<T> collection)
        {
            _queue = new Queue<T>(collection);
        }

        public MessagePassingQueue(int capacity)
        {
            _queue = new Queue<T>(capacity);
        }

        public void Enqueue(T item)
        {
            lock (_lockObj)
            {
                _queue.Enqueue(item);
            }

            _queuedItemSignal.Set();
        }

        public bool TryDequeue(out T item)
        {
            lock (_lockObj)
            {
                if (_queue.Count == 0)
                {
                    item = default(T);
                    return false;
                }

                item = _queue.Dequeue();
                return true;
            }
        }

        /// <summary>
        /// Removes an and returns the item at the beginning of the queue.
        /// If the queue is empty, this will block until there is an item to remove.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public T Dequeue()
        {
            while (true)
            {
                lock (_lockObj)
                {
                    if (_queue.Count > 0)
                    {
                        return _queue.Dequeue();
                    }
                }

                _queuedItemSignal.WaitOne();
            }
        }

        public void Clear()
        {
            lock (_lockObj)
            {
                _queue.Clear();
            }
        }
    }
}
