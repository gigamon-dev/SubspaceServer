using System;
using System.Diagnostics;
using System.Threading;

namespace SS.Utilities
{
    /// <summary>
    /// Base class for an object that knows what <see cref="Pool{T}"/> it originated from.
    /// It implements <see cref="IDisposable"/> such that calling <see cref="Dispose"/> will return the object to it's pool.
    /// That is, it uses a pattern similar to what <see cref="System.Data.SqlClient"/> uses for connection pooling.
    /// This allows us to pass the object around, effectively transferring 'ownership', such that the 'owner' is responsible for disposing it without having to know which pool to return it to.
    /// </summary>
    public abstract class PooledObject : IDisposable
    {
        private int _isAvailableInt = 0; // boolean, but toggling in a lock-free manner

        /// <summary>
        /// The pool that this object originated from.
        /// </summary>
        internal IPoolInternal? Pool { get; init; }

        /// <summary>
        /// Set the availablity of the object (whether it's in the pool).
        /// This is used to protect against duplicate additions or removals of an object from a pool which would indicate a programming mistake.
        /// </summary>
        /// <param name="available">
        /// True to mark the object as being available to get from the pool.
        /// False to indicate that the object has been taken from the pool and is in use.
        /// </param>
        /// <returns></returns>
        internal bool TrySetAvailability(bool available)
        {
            int newValue = available ? 1 : 0;
            int expectedValue = available ? 0 : 1;
            int originalValue = Interlocked.CompareExchange(ref _isAvailableInt, newValue, expectedValue);

            Debug.Assert(originalValue == expectedValue, $"Detected a duplicate attempt to {(available ? "add" : "remove")} the object {(available ? "to" : "from")} its pool.");
            return originalValue == expectedValue;
        }

        #region IDisposable Members

        /// <summary>
        /// Returns the object to its originating pool.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "Dispose is being used as a pooling mechanism for object reuse. Not actually disposing the object.")]
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        /// <summary>
        /// Derived classes overriding this method should remember to call down to the base class method so that the object is returned to the pool it originated from.
        /// </summary>
        /// <param name="isDisposing"></param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                Pool?.Release(this); // return this object to the pool it originated from
            }
        }
    }
}
