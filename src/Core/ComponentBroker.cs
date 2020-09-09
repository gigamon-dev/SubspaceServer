using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// Base interface for interfaces that are registerable with the ComponentBroker.
    /// </summary>
    public interface IComponentInterface
    {
    }

    /// <summary>
    /// Functions as an intermediary between components.
    /// It currently manages interfaces and callbacks.
    /// </summary>
    public class ComponentBroker
    {
        protected ComponentBroker() : this(null)
        {
        }

        protected ComponentBroker(ComponentBroker parent)
        {
            Parent = parent;
        }

        /// <summary>
        /// The parent broker. <see langword="null" /> means there's no parent.
        /// </summary>
        public ComponentBroker Parent { get; }

        #region Interface Methods

        private object _interfaceLockObj = new object();
        private Dictionary<Type, IComponentInterface> _interfaceLookup = new Dictionary<Type, IComponentInterface>();
        private Dictionary<Type, int> _interfaceReferenceLookup = new Dictionary<Type, int>();

        public void RegisterInterface<TInterface>(TInterface implementor) where TInterface : IComponentInterface
        {
            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            //int hash = t.GetHashCode();
            //int impHash = implementor.GetHashCode();

            lock (_interfaceLockObj)
            {
#if DEBUG
                // TODO: probably should throw an exception if the interface is already registered
                if (_interfaceLookup.ContainsKey(interfaceType))
                    Console.WriteLine("registering an interface that already has been registered (overwriting existing) [{0}]", interfaceType.FullName);
#endif
                // override any existing implementation of the interface
                _interfaceLookup[interfaceType] = implementor;
            }
        }

        public int UnregisterInterface<TInterface>() where TInterface : IComponentInterface
        {
            Type interfaceType = typeof(TInterface);
            int referenceCount;

            lock (_interfaceLockObj)
            {
                if (_interfaceReferenceLookup.TryGetValue(interfaceType, out referenceCount) == true)
                {
                    if (referenceCount > 0)
                        return referenceCount; // reference count > 0, can't unregister

                    _interfaceReferenceLookup.Remove(interfaceType);
                }

                // unregister
                _interfaceLookup.Remove(interfaceType);
                return 0;
            }
        }

        public virtual IComponentInterface GetInterface(Type interfaceType)
        {
            if (interfaceType == null)
                throw new ArgumentNullException("interfaceType");

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("type must be an interface", "interfaceType");

            lock (_interfaceLockObj)
            {
                IComponentInterface theInterface;
                if (_interfaceLookup.TryGetValue(interfaceType, out theInterface) == false)
                    return null;

                // found the specified interface, increment the reference count
                int referenceCount;
                if (_interfaceReferenceLookup.TryGetValue(interfaceType, out referenceCount) == true)
                {
                    _interfaceReferenceLookup[interfaceType] = referenceCount + 1;
                }
                else
                {
                    // first reference
                    _interfaceReferenceLookup.Add(interfaceType, 1);
                }

                return theInterface;
            }
        }

        public virtual TInterface GetInterface<TInterface>() where TInterface : class, IComponentInterface
        {
            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(string.Format("type is not an interface [{0}]", interfaceType.FullName));

            lock (_interfaceLockObj)
            {
                IComponentInterface theInterface;
                if (_interfaceLookup.TryGetValue(interfaceType, out theInterface) == false)
                    return null;

                TInterface theConcreteInterface = theInterface as TInterface;
                if (theConcreteInterface == null)
                    return null;

                // found the specified interface
                if (_interfaceReferenceLookup.ContainsKey(interfaceType))
                {
                    _interfaceReferenceLookup[interfaceType]++;
                }
                else
                {
                    // first reference
                    _interfaceReferenceLookup.Add(interfaceType, 1);
                }

                return theConcreteInterface;
            }
        }

        protected virtual void ReleaseInterface(Type interfaceType)
        {
            int referenceCount;

            lock (_interfaceLockObj)
            {
                if (_interfaceReferenceLookup.TryGetValue(interfaceType, out referenceCount) == true)
                {
                    _interfaceReferenceLookup[interfaceType] = (referenceCount > 0) ? referenceCount - 1 : 0;
                }
            }
        }

        public virtual void ReleaseInterface<TInterface>()
        {
            Type t = typeof(TInterface);
            int referenceCount;

            lock (_interfaceLockObj)
            {
                if (_interfaceReferenceLookup.TryGetValue(t, out referenceCount) == true)
                {
                    _interfaceReferenceLookup[t] = (referenceCount > 0) ? referenceCount - 1 : 0;
                }
            }
        }

        #endregion

        #region Callback Methods

        /// <summary>
        /// For synchronizing access to the <see cref="_callbackLookup"/>.
        /// </summary>
        private ReaderWriterLockSlim _callbackRwLock = new ReaderWriterLockSlim();

        /// <summary>
        /// The callback dictionary where: Key is the delegate type. Value is the delegate itself.
        /// </summary>
        private Dictionary<Type, Delegate> _callbackLookup = new Dictionary<Type, Delegate>();

        /// <summary>
        /// Registers a handler for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <param name="handler">The handler to register.</param>
        public void RegisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate
        {
            if (handler == null)
                throw new ArgumentNullException("handler");

            Type key = typeof(TDelegate);
            
            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackLookup.TryGetValue(key, out Delegate d))
                {
                    _callbackLookup[key] = Delegate.Combine(d, handler);
                }
                else
                {
                    _callbackLookup.Add(key, handler);
                }
            }
            finally
            {
                _callbackRwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Unregisters a handler for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <param name="handler">The handler to un-register.</param>
        public void UnregisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate
        {
            if (handler == null)
                throw new ArgumentNullException("handler");

            Type key = typeof(TDelegate);

            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackLookup.TryGetValue(key, out Delegate d) == false)
                {
                    return;
                }

                d = Delegate.Remove(d, handler);

                if (d == null)
                    _callbackLookup.Remove(key);
                else
                    _callbackLookup[key] = d;
            }
            finally
            {
                _callbackRwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets the current delegate for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <returns>The delegate if found. Otherwise null.</returns>
        public TDelegate GetCallback<TDelegate>() where TDelegate : Delegate
        {
            Type key = typeof(TDelegate);

            _callbackRwLock.EnterReadLock();

            try
            {
                if (!_callbackLookup.TryGetValue(key, out Delegate d))
                {
                    return null;
                }

                return d as TDelegate;
            }
            finally
            {
                _callbackRwLock.ExitReadLock();
            }
        }

        #endregion
    }
}
