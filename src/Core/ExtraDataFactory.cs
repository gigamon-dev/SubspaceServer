using Microsoft.Extensions.ObjectPool;
using System;

namespace SS.Core
{
    /// <summary>
    /// Interface that allows "extra data" (per-player extra data or per-arena extra data) to be pooled.
    /// </summary>
    public interface IPooledExtraData
    {
        /// <summary>
        /// Resets the extra data object back to its original starting state.
        /// </summary>
        /// <remarks>
        /// This is called when the object is returned to its pool.
        /// Implementors should perform any needed cleanup and reset all of the object's data members
        /// to a state equivalent to as if the object were just constructed.
        /// </remarks>
        void Reset();
    }

    /// <summary>
    /// A <see cref="PooledObjectPolicy{T}"/> that handles resetting of objects when <see cref="IPooledExtraData"/> is implemented.
    /// </summary>
    /// <typeparam name="T">The type of extra data object.</typeparam>
    internal class DefaultPooledExtraDataPooledObjectPolicy<T> : PooledObjectPolicy<T> where T : class, new()
    {
        public override T Create()
        {
            return new T();
        }

        public override bool Return(T obj)
        {
            if (obj is IPooledExtraData pooledExtraData)
            {
                pooledExtraData.Reset();
            }

            return true;
        }
    }

    /// <summary>
    /// Abstract class for a factory that provides "extra data" objects.
    /// </summary>
    internal abstract class ExtraDataFactory : IDisposable
    {
        /// <summary>
        /// Gets an "extra data" object.
        /// </summary>
        /// <returns>The extra data object.</returns>
        public abstract object Get();

        /// <summary>
        /// Returns a no longer needed "extra data" object.
        /// </summary>
        /// <param name="obj">The extra data object to return.</param>
        public abstract void Return(object obj);

        /// <remarks>
        /// Implementations that pool objects dispose the object pool and the objects in that pool.
        /// </remarks>
        protected abstract void Dispose(bool disposing);

        /// <summary>
        /// Disposes the factory.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Factory for providing "extra data" objects that are not pooled.
    /// </summary>
    /// <typeparam name="T">The type of extra data object.</typeparam>
    internal class NonPooledExtraDataFactory<T> : ExtraDataFactory where T : class, new()
    {
        public override object Get()
        {
            return new T();
        }

        public override void Return(object obj)
        {
            if (obj is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            // no-op
        }
    }

    /// <summary>
    /// Factory for providing "extra" data objects that are pooled.
    /// </summary>
    /// <typeparam name="T">The type of extra data object.</typeparam>
    internal class PooledExtraDataFactory<T> : ExtraDataFactory where T : class
    {
        protected readonly ObjectPool<T> Pool;

        /// <summary>
        /// Initializes a new <see cref="PooledExtraDataFactory{T}"/>.
        /// </summary>
        /// <param name="provider">
        /// The object pool provider. 
        /// This is purposely a <see cref="DefaultObjectPoolProvider"/> since it's the only provider that can create a DisposableObjectPool&lt;T&gt; which is internal to Microsoft.Extensions.ObjectPool.
        /// </param>
        /// <param name="policy">The policy to use for the pooled objects.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> was null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="policy"/> was null.</exception>
        protected PooledExtraDataFactory(DefaultObjectPoolProvider provider, IPooledObjectPolicy<T> policy)
        {
            if (provider is null)
                throw new ArgumentNullException(nameof(provider));

            if (policy is null)
                throw new ArgumentNullException(nameof(policy));

            Pool = provider.Create(policy);
        }

        public override object Get()
        {
            return Pool.Get();
        }

        public override void Return(object obj)
        {
            if (obj is T toReturn)
            {
                Pool.Return(toReturn);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing
                && Pool is IDisposable disposable)
            {
                // Note: The DefaultObjectPoolProvider creates a DisposableObjectPool<T> when T implements IDisposable.
                // This will dispose the pool and the objects it contains.
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Factory for providing "extra data" objects that are pooled using the <see cref="DefaultPooledExtraDataPooledObjectPolicy{T}"/>.
    /// </summary>
    /// <remarks>Using the <see cref="DefaultPooledExtraDataPooledObjectPolicy{T}"/> means <typeparamref name="T"/> needs to have a default constructor.</remarks>
    /// <typeparam name="T">The type of extra data object.</typeparam>
    internal class DefaultPooledExtraDataFactory<T> : PooledExtraDataFactory<T> where T : class, new()
    {
        public DefaultPooledExtraDataFactory(DefaultObjectPoolProvider provider)
            : base(provider, new DefaultPooledExtraDataPooledObjectPolicy<T>())
        {
        }
    }

    /// <summary>
    /// Factory for providing "extra data" objects that are pooled using a specified <see cref="IPooledObjectPolicy{T}"/>.
    /// </summary>
    /// <remarks>Using a policy means <typeparamref name="T"/> does not need to have default constructor, the policy constructs the objects.</remarks>
    /// <typeparam name="T">The type of extra data object.</typeparam>
    internal class CustomPooledExtraDataFactory<T> : PooledExtraDataFactory<T> where T : class
    {
        public CustomPooledExtraDataFactory(DefaultObjectPoolProvider provider, IPooledObjectPolicy<T> policy)
            : base(provider, policy)
        {
        }
    }
}
