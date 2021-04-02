using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// Base interface for interfaces that are registerable with the <see cref="ComponentBroker"/>.
    /// </summary>
    public interface IComponentInterface
    {
    }

    /// <summary>
    /// Identifies an interface registration.
    /// Used to unregister a previous registration.
    /// </summary>
    public abstract class InterfaceRegistrationToken
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

        /// <summary>
        /// Interface registration data
        /// </summary>
        private class InterfaceData
        {
            /// <summary>
            /// The instance that is registered.
            /// </summary>
            public IComponentInterface Instance { get; }

            /// <summary>
            /// An optional name for the instance. Usually null, but this allows registering multiple instances
            /// for the same same interface type, and retrieve them by name.
            /// E.g., This might be useful for network encryption modules.  Register an instance for "VIE" encrption and another for "Continuum" encryption.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// A token that uniquely identifies a registration.
            /// Returned when an interface is registered and later used to unregister. Only the one who registered will have it, no others can interfere.
            /// </summary>
            public InterfaceRegistrationToken RegistrationToken { get; } = new ConcreteInterfaceRegistrationToken();

            /// <summary>
            /// A count of how many active references to the instance there are.
            /// </summary>
            public int ReferenceCount = 0;

            public InterfaceData(IComponentInterface instance, string name)
            {
                Instance = instance ?? throw new ArgumentNullException(nameof(instance));
                Name = name;
            }
        }

        private class ConcreteInterfaceRegistrationToken : InterfaceRegistrationToken
        {
        }

        /// <summary>
        /// For synchronizing access to <see cref="_interfaceRegistrations"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _interfaceRwLock = new ReaderWriterLockSlim();

        /// <summary>
        /// Dictionary of interface registartions where:
        /// Key is the <see cref="Type"/> of the interface.
        /// Value is a LinkedList of registrations.  
        /// <para>
        /// In the case of an interface getting registered more than once (with the same name or no name), a node earlier in the list "overrides" similar registrations that come after it.
        /// </para>
        /// </summary>
        private readonly Dictionary<Type, LinkedList<InterfaceData>> _interfaceRegistrations = new Dictionary<Type, LinkedList<InterfaceData>>();

        /// <summary>
        /// Registers an implementation of an interface to be exposed to others via the broker.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to register.</typeparam>
        /// <param name="instance">The implementer instance to register.</param>
        /// <param name="name">An optional name to register the <paramref name="instance"/> as.</param>
        /// <returns></returns>
        public InterfaceRegistrationToken RegisterInterface<TInterface>(TInterface instance, string name = null) where TInterface : class, IComponentInterface
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            InterfaceData iData = new InterfaceData(instance, name);

            _interfaceRwLock.EnterWriteLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList) == false)
                {
                    registrationList = new LinkedList<InterfaceData>();
                    _interfaceRegistrations.Add(interfaceType, registrationList);
                }

                registrationList.AddFirst(iData); // first in the list overrides any that are already registered
                return iData.RegistrationToken;
            }
            finally
            {
                _interfaceRwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Unregisters an implementation of an interface.
        /// It will refuse to unregister if the reference count indicates it it still in use.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to un-register.</typeparam>
        /// <param name="token">The unique token that was returned from <see cref="RegisterInterface{TInterface}(TInterface, string)"/>.</param>
        /// <returns>The reference count. Therefore, 0 means success.</returns>
        public int UnregisterInterface<TInterface>(ref InterfaceRegistrationToken token) where TInterface : class, IComponentInterface
        {
            if (token == null)
                throw new ArgumentNullException(nameof(token));

            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            _interfaceRwLock.EnterWriteLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList) == false)
                {
                    // no record of registration for the interface
                    return 0;
                }

                LinkedListNode<InterfaceData> node = registrationList.First;
                while (node != null)
                {
                    if (node.Value.RegistrationToken == token)
                        break;

                    node = node.Next;
                }

                if (node == null)
                {
                    // no record of registration matching the token
                    return 0;
                }

                if (node.Value.ReferenceCount > 0)
                {
                    // still being referenced, can't unregister
                    return node.Value.ReferenceCount;
                }

                registrationList.Remove(node);

                if (registrationList.Count == 0)
                {
                    _interfaceRegistrations.Remove(interfaceType);
                }

                // successfully unregistered
                token = null;
                return 0;
            }
            finally
            {
                _interfaceRwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Retrieves the currently registered instance that implements an interface.
        /// Remember to call <see cref="ReleaseInterface"/> when done using it.
        /// </summary>
        /// <param name="interfaceType">The <see cref="Type"/> of interface to retrieve.</param>
        /// <param name="name">
        /// An optional name of the instance to get.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        /// <returns>The currently registered instance.  Otherwise, null.</returns>
        public IComponentInterface GetInterface(Type interfaceType, string name = null)
        {
            if (interfaceType == null)
                throw new ArgumentNullException(nameof(interfaceType));

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("Must be an interface.", nameof(interfaceType));

            if (typeof(IComponentInterface).IsAssignableFrom(interfaceType) == false)
                throw new ArgumentException("Must be an IComponentInterface.", nameof(interfaceType));

            //
            // Try to get it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList))
                {
                    LinkedListNode<InterfaceData> node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Name == name)
                            break;

                        node = node.Next;
                    }

                    if (node != null)
                    {
                        InterfaceData iData = node.Value;
                        Interlocked.Increment(ref iData.ReferenceCount);
                        return iData.Instance;
                    }
                }
            }
            finally
            {
                _interfaceRwLock.ExitReadLock();
            }

            //
            // Otherwise, try to get it from the parent.
            //

            return Parent?.GetInterface(interfaceType, name);
        }

        /// <summary>
        /// Retrieves the currently registered instance that implements an interface.
        /// Remember to call <see cref="ReleaseInterface"/> when done using it.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to retrieve.</typeparam>
        /// <param name="name">
        /// An optional name of the instance to get.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        /// <returns>The currently registered instance.  Otherwise, null.</returns>
        public TInterface GetInterface<TInterface>(string name = null) where TInterface : class, IComponentInterface
        {
            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            //
            // Try to get it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList))
                {
                    LinkedListNode<InterfaceData> node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Name == name)
                            break;

                        node = node.Next;
                    }

                    if (node != null)
                    {
                        InterfaceData iData = node.Value;

                        if (iData.Instance is TInterface instance)
                        {
                            Interlocked.Increment(ref iData.ReferenceCount);
                            return instance;
                        }
                    }
                }
            }
            finally
            {
                _interfaceRwLock.ExitReadLock();
            }

            //
            // Otherwise, try to get it from the parent.
            //

            return Parent?.GetInterface<TInterface>(name);
        }

        /// <summary>
        /// Releases an interface.
        /// </summary>
        /// <param name="interfaceType">The <see cref="Type"/> of interface to release.</param>
        /// <param name="instance">The instance to release.</param>
        /// <param name="name">
        /// An optional name of the instance to release.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        public void ReleaseInterface(Type interfaceType, IComponentInterface instance, string name = null)
        {
            if (interfaceType == null)
                throw new ArgumentNullException(nameof(interfaceType));

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("Must be an interface.", nameof(interfaceType));

            if(typeof(IComponentInterface).IsAssignableFrom(interfaceType) == false)
                throw new ArgumentException("Must be an IComponentInterface.", nameof(interfaceType));

            if (instance == null)
                return; // nothing to release

            //
            // Try to release it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList))
                {
                    LinkedListNode<InterfaceData> node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Instance == instance && node.Value.Name == name)
                            break;

                        node = node.Next;
                    }

                    if (node != null)
                    {
                        Interlocked.Decrement(ref node.Value.ReferenceCount);
                        return;
                    }
                }
            }
            finally
            {
                _interfaceRwLock.ExitReadLock();
            }

            //
            // Otherwise, try to release it from the parent.
            //

            Parent?.ReleaseInterface(interfaceType, instance, name);
        }

        /// <summary>
        /// Releases an interface.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to release.</typeparam>
        /// <param name="instance">The instance to release.</param>
        /// <param name="name">
        /// An optional name of the instance to release.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        public void ReleaseInterface<TInterface>(ref TInterface instance, string name = null) where TInterface : class, IComponentInterface
        {
            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            if (instance == null)
                return; // nothing to release

            //
            // Try to release it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData> registrationList))
                {
                    LinkedListNode<InterfaceData> node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Instance == instance && node.Value.Name == name)
                            break;

                        node = node.Next;
                    }

                    if (node != null)
                    {
                        Interlocked.Decrement(ref node.Value.ReferenceCount);
                        instance = null;
                        return;
                    }
                }
            }
            finally
            {
                _interfaceRwLock.ExitReadLock();
            }

            //
            // Otherwise, try to release it from the parent.
            //

            Parent?.ReleaseInterface(ref instance, name);
        }

        #endregion

        #region Callback Methods

        /// <summary>
        /// For synchronizing access to the <see cref="_callbackRegistrations"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _callbackRwLock = new ReaderWriterLockSlim();

        /// <summary>
        /// The callback dictionary where: Key is the delegate type. Value is the delegate itself.
        /// </summary>
        private readonly Dictionary<Type, Delegate> _callbackRegistrations = new Dictionary<Type, Delegate>();

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
                throw new ArgumentNullException(nameof(handler));

            Type key = typeof(TDelegate);
            
            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackRegistrations.TryGetValue(key, out Delegate d))
                {
                    _callbackRegistrations[key] = Delegate.Combine(d, handler);
                }
                else
                {
                    _callbackRegistrations.Add(key, handler);
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
                throw new ArgumentNullException(nameof(handler));

            Type key = typeof(TDelegate);

            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackRegistrations.TryGetValue(key, out Delegate d) == false)
                {
                    return;
                }

                d = Delegate.Remove(d, handler);

                if (d == null)
                    _callbackRegistrations.Remove(key);
                else
                    _callbackRegistrations[key] = d;
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
                if (!_callbackRegistrations.TryGetValue(key, out Delegate d))
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
