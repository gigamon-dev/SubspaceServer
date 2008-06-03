using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.Utilities
{
    /// <summary>
    /// A simple thread safe queue.
    /// Similar to asss' MPQueue.
    /// 
    /// Supports synchronization of multiple Enqueuing (writing) threads and multiple Dequeueing (reading) threads.
    /// </summary>
    /// <typeparam name="T">type of object to store in the queue</typeparam>
    public class MessagePassingQueue<T>
    {
        /// <summary>
        /// object used to synchronize access to the queue
        /// </summary>
        private object _syncObj = new object();

        private Queue<T> _queue; // TODO: maybe change this to a linked list

        /// <summary>
        /// create an empty queue
        /// </summary>
        public MessagePassingQueue()
        {
            _queue = new Queue<T>();
        }

        /// <summary>
        /// creates a queue prepopulated with items
        /// </summary>
        /// <param name="collection">items to prepopulate the queue with</param>
        public MessagePassingQueue(IEnumerable<T> collection)
        {
            _queue = new Queue<T>(collection);
        }

        /// <summary>
        /// creates a queue, specifying the capacity of the queue
        /// </summary>
        /// <param name="capacity">the initial # of items the queue can contain</param>
        public MessagePassingQueue(int capacity)
        {
            _queue = new Queue<T>(capacity);
        }

        /// <summary>
        /// To add an object to the queue.
        /// Any threads waiting for a message from the queue will be woken up.
        /// </summary>
        /// <param name="item">item to add to the queue</param>
        public void Enqueue(T item)
        {
            lock (_syncObj)
            {
                _queue.Enqueue(item);

                Monitor.Pulse(_syncObj);
            }
        }

        /// <summary>
        /// To try to read (and remove) the next item in the queue.
        /// </summary>
        /// <param name="item">
        /// When this method returns, contains the item read from the queue.  
        /// If no item is read, it will contain the default value for the type.
        /// </param>
        /// <returns>true if an item was read, otherwise false</returns>
        public bool TryDequeue(out T item)
        {
            lock (_syncObj)
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
        /// To read (and remove) the next item in the queue.
        /// If the queue is empty, this will block until there is an item to remove.
        /// </summary>
        /// <returns>The item read from the queue.</returns>
        public T Dequeue()
        {
            lock (_syncObj)
            {
                while (true)
                {
                    if (_queue.Count > 0)
                    {
                        return _queue.Dequeue();
                    }

                    Monitor.Wait(_syncObj);
                }
            }
        }

        /// <summary>
        /// To remove all items from the queue without reading them.
        /// </summary>
        public void Clear()
        {
            lock (_syncObj)
            {
                _queue.Clear();
            }
        }
    }
}
