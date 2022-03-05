using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// Base interface for interfaces that can be registered on a <see cref="ComponentBroker"/>.
    /// </summary>
    public interface IComponentInterface
    {
    }

    /// <summary>
    /// Base interface for advisors that can be registered on a <see cref="ComponentBroker"/>.
    /// </summary>
    public interface IComponentAdvisor
    {
    }

    /// <summary>
    /// Identifies an interface registration.
    /// </summary>
    /// <remarks>
    /// One is returned upon registering an interface, which can be used unregister the interface.
    /// </remarks>
    /// <typeparam name="T">The <see cref="IComponentInterface"/> type that the registration is for..</typeparam>
    public abstract class InterfaceRegistrationToken<T> where T : IComponentInterface
    {
    }

    /// <summary>
    /// Identifies an advisor registration.
    /// </summary>
    /// <remarks>
    /// One is returned upon registering an advisor, which can be used to unregister the advisor.
    /// A token only allows for a single successful use.
    /// </remarks>
    /// <typeparam name="T">The <see cref="IComponentAdvisor"/> type that the registration is for.</typeparam>
    public abstract class AdvisorRegistrationToken<T> where T : IComponentAdvisor
    {
    }

    /// <summary>
    /// Functions as an intermediary between components by managing interfaces, callbacks, and advisors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref=" ComponentBroker"/> acts as a scope for the components it manages.
    /// The server has a single root <see cref=" ComponentBroker"/> which acts as the "global" scope.
    /// This root <see cref=" ComponentBroker"/> is actually the <see cref="ModuleManager"/>.
    /// Each <see cref="Arena"/> is also a <see cref="ComponentBroker"/>, with the root being the parent.
    /// </para>
    /// <para>
    /// Interfaces are how components expose their functionality to other components.
    /// A module can implement an interface and register the interface on a <see cref="ComponentBroker"/> to expose it to modules.
    /// A module can use a <see cref="ComponentBroker"/> to obtain interfaces of other modules too.
    /// </para>
    /// <para>
    /// Callbacks are an implementation of the publisher-subscriber pattern where
    /// any component can be a publisher, and any component can be a subscriber. 
    /// There can be multiple publishers and multiple subscribers.
    /// Registering for a callback on an <see cref="Arena"/> means you only want events for that specific arena.
    /// Registering for a callback on the root <see cref="ComponentBroker"/> means you want all events, including those fired for an arena.
    /// </para>
    /// <para>
    /// Advisors are interfaces that are expected to possibly have more than one implementation.
    /// So, using advisors actually means getting a collection of implementations for a specified advsior interface type, 
    /// and then asking each implementation in the collection for advice on how to proceed with a given task.
    /// </para>
    /// </remarks>
    public class ComponentBroker
    {
        protected ComponentBroker() : this(null)
        {
        }

        protected ComponentBroker(ComponentBroker parent)
        {
            Parent = parent;

            if (Parent != null)
            {
                Parent.AdvisorChanged += Parent_AdvisorChanged;
            }
        }

        /// <summary>
        /// The parent broker. <see langword="null" /> means there's no parent.
        /// </summary>
        public ComponentBroker Parent { get; }

        #region Interface Methods

        /// <summary>
        /// Interface registration data
        /// </summary>
        private abstract class InterfaceData
        {
            /// <summary>
            /// The instance that is registered.
            /// </summary>
            public abstract IComponentInterface Instance { get; }

            /// <summary>
            /// An optional name for the instance. Usually null, but this allows registering multiple instances
            /// for the same same interface type, and retrieve them by name.
            /// E.g., This might be useful for network encryption modules.  Register an instance for "VIE" encrption and another for "Continuum" encryption.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// A count of how many active references to the instance there are.
            /// </summary>
            public int ReferenceCount = 0;

            public InterfaceData(string name)
            {
                Name = name;
            }
        }

        private class InterfaceData<T> : InterfaceData where T : IComponentInterface
        {
            private readonly T _instance;

            public override IComponentInterface Instance => _instance;

            /// <summary>
            /// A token that uniquely identifies a registration.
            /// Returned when an interface is registered and later used to unregister. Only the one who registered will have it, no others can interfere.
            /// </summary>
            public InterfaceRegistrationToken<T> RegistrationToken { get; } = new ConcreteInterfaceRegistrationToken<T>();

            public InterfaceData(T instance, string name) : base(name)
            {
                _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            }
        }

        private class ConcreteInterfaceRegistrationToken<T> : InterfaceRegistrationToken<T> where T : IComponentInterface
        {
        }

        /// <summary>
        /// For synchronizing access to <see cref="_interfaceRegistrations"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _interfaceRwLock = new();

        /// <summary>
        /// Dictionary of interface registartions where:
        /// Key is the <see cref="Type"/> of the interface.
        /// Value is a LinkedList of registrations.  
        /// <para>
        /// In the case of an interface getting registered more than once (with the same name or no name), a node earlier in the list "overrides" similar registrations that come after it.
        /// </para>
        /// </summary>
        private readonly Dictionary<Type, LinkedList<InterfaceData>> _interfaceRegistrations = new();

        /// <summary>
        /// Registers an implementation of an interface to be exposed to others via the broker.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to register.</typeparam>
        /// <param name="instance">The implementer instance to register.</param>
        /// <param name="name">An optional name to register the <paramref name="instance"/> as.</param>
        /// <returns></returns>
        public InterfaceRegistrationToken<TInterface> RegisterInterface<TInterface>(TInterface instance, string name = null) where TInterface : class, IComponentInterface
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            InterfaceData<TInterface> iData = new(instance, name);

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
        public int UnregisterInterface<TInterface>(ref InterfaceRegistrationToken<TInterface> token) where TInterface : class, IComponentInterface
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
                    if (node.Value is InterfaceData<TInterface> interfaceData && interfaceData.RegistrationToken == token)
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
        private readonly ReaderWriterLockSlim _callbackRwLock = new();

        /// <summary>
        /// The callback dictionary where: Key is the delegate type. Value is the delegate itself.
        /// </summary>
        private readonly Dictionary<Type, Delegate> _callbackRegistrations = new();

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

        #region Advisor methods

        private class ConcreteAdvisorRegistrationToken<T> : AdvisorRegistrationToken<T> where T : IComponentAdvisor
        {
            /// <summary>
            /// The broker that created the token.
            /// </summary>
            public readonly ComponentBroker Broker;

            /// <summary>
            /// The advisor that was registered.
            /// </summary>
            public readonly T Instance;

            /// <summary>
            /// Whether the token is still active. A token can be used to unregister a previously registered advisor once and only once.
            /// </summary>
            public bool IsActive { get; private set; }

            public ConcreteAdvisorRegistrationToken(ComponentBroker broker, T instance)
            {
                Broker = broker ?? throw new ArgumentNullException(nameof(broker));
                Instance = instance ?? throw new ArgumentNullException(nameof(instance));
                IsActive = true;
            }

            /// <summary>
            /// Marks the token as having been used.
            /// </summary>
            public void Deactivate()
            {
                IsActive = false;
            }
        }

        private abstract class AdvisorData
        {
            public abstract void RefreshCombined(ComponentBroker parent);
        }

        private class AdvisorData<TAdvisor> : AdvisorData where TAdvisor : IComponentAdvisor
        {
            /// <summary>
            /// The registered advisors.
            /// </summary>
            public ImmutableArray<TAdvisor> Registered { get; private set; } = ImmutableArray<TAdvisor>.Empty;

            /// <summary>
            /// <see cref="Registered"/> combined with those from parent.
            /// </summary>
            public ImmutableArray<TAdvisor> Advisors { get; private set; } = ImmutableArray<TAdvisor>.Empty;

            public void AddAndRecombine(TAdvisor toAdd, ComponentBroker parent)
            {
                if (toAdd != null)
                {
                    Registered = Registered.Add(toAdd);
                }

                RefreshCombined(parent);
            }

            public bool RemoveAndRecombine(TAdvisor toRemove, ComponentBroker parent)
            {
                if (toRemove == null)
                    return false;

                int lengthBefore = Registered.Length;
                Registered = Registered.Remove(toRemove);
                if (lengthBefore == Registered.Length)
                    return false;

                RefreshCombined(parent);
                return true;
            }

            public override void RefreshCombined(ComponentBroker parent)
            {
                if (parent != null)
                {
                    Advisors = Registered.AddRange(parent.GetAdvisors<TAdvisor>());
                }
                else
                {
                    Advisors = Registered;
                }
            }
        }

        private readonly Dictionary<Type, AdvisorData> _advisorDictionary = new();
        private readonly ReaderWriterLockSlim _advisorLock = new();

        private event Action<Type> AdvisorChanged;

        private void Parent_AdvisorChanged(Type advisorType)
        {
            _advisorLock.EnterWriteLock();

            try
            {
                if (_advisorDictionary.TryGetValue(advisorType, out AdvisorData advisorData))
                {
                    advisorData.RefreshCombined(Parent);
                }
            }
            finally
            {
                _advisorLock.ExitWriteLock();
            }

            AdvisorChanged?.Invoke(advisorType);
        }

        /// <summary>
        /// Registers an advisor.
        /// </summary>
        /// <typeparam name="TAdvisor">The type of advisor to register.</typeparam>
        /// <param name="advisor">The advisor to register.</param>
        /// <returns>A token that can be used to unregister the advisor.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="advisor"/> was null.</exception>
        public AdvisorRegistrationToken<TAdvisor> RegisterAdvisor<TAdvisor>(TAdvisor advisor) where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            if (advisor == null)
                throw new ArgumentNullException(nameof(advisor));

            _advisorLock.EnterWriteLock();

            try
            {
                if (!_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData advisorData)
                    || advisorData is not AdvisorData<TAdvisor> tAdvisorData)
                {
                    tAdvisorData = new AdvisorData<TAdvisor>();
                    _advisorDictionary.Add(typeof(TAdvisor), tAdvisorData);
                }

                tAdvisorData.AddAndRecombine(advisor, Parent);
            }
            finally
            {
                _advisorLock.ExitWriteLock();
            }

            AdvisorChanged?.Invoke(typeof(TAdvisor));

            return new ConcreteAdvisorRegistrationToken<TAdvisor>(this, advisor);
        }

        /// <summary>
        /// Unregisters an advisor.
        /// </summary>
        /// <typeparam name="TAdvisor">The type of advisor to unregister.</typeparam>
        /// <param name="token">Token of the advisor to unregister.</param>
        /// <returns>True if the advisor was unregistered. Otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="advisor"/> was null.</exception>
        public bool UnregisterAdvisor<TAdvisor>(ref AdvisorRegistrationToken<TAdvisor> token) where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            if (token == null)
                throw new ArgumentNullException(nameof(token));

            if (token is not ConcreteAdvisorRegistrationToken<TAdvisor> concreteToken)
                throw new ArgumentException("Not a valid token.", nameof(token));

            if (concreteToken.Broker != this)
                throw new ArgumentException("The token is not for this ComponentBroker.", nameof(token));

            if (!concreteToken.IsActive)
                throw new ArgumentException("The token was already used.", nameof(token));

            _advisorLock.EnterWriteLock();

            try
            {
                // double check now that we have the lock
                if (!concreteToken.IsActive)
                    throw new ArgumentException("Token was already used.", nameof(token));

                if (!_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData advisorData)
                    || advisorData is not AdvisorData<TAdvisor> tAdvisorData)
                {
                    return false;
                }

                if (!tAdvisorData.RemoveAndRecombine(concreteToken.Instance, Parent))
                {
                    return false;
                }

                concreteToken.Deactivate();

                if (tAdvisorData.Registered.IsEmpty)
                {
                    _advisorDictionary.Remove(typeof(TAdvisor));
                }
            }
            finally
            {
                _advisorLock.ExitWriteLock();
            }

            AdvisorChanged?.Invoke(typeof(TAdvisor));

            token = null;
            return true;
        }

        /// <summary>
        /// Gets a collection of advisors that have been registered.
        /// </summary>
        /// <remarks>
        /// The advisors returned will include any registered on this <see cref="ComponentBroker"/> instance and those from any parent <see cref="ComponentBroker"/>.
        /// In other words, calling this method on an <see cref="Arena"/> will get those registered on the arena level and on the global level.
        /// Calling this method on the global <see cref="ComponentBroker"/> will return only those registered on the global level.
        /// </remarks>
        /// <typeparam name="TAdvisor">The type of advisors to get.</typeparam>
        /// <returns>A collection of advisors. The collection is purposely thread-safe.</returns>
        public ImmutableArray<TAdvisor> GetAdvisors<TAdvisor>() where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            _advisorLock.EnterReadLock();

            try
            {
                if (_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData advisorData)
                    && advisorData is AdvisorData<TAdvisor> tAdvisorData)
                {
                    return tAdvisorData.Advisors;
                }
            }
            finally
            {
                _advisorLock.ExitReadLock();
            }

            if (Parent != null)
            {
                return Parent.GetAdvisors<TAdvisor>();
            }
            else
            {
                return ImmutableArray<TAdvisor>.Empty;
            }
        }

        #endregion
    }
}
