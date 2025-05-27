using SS.Core.ComponentInterfaces;
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
    /// Attribute that instructs the source generator to write helper methods for registering, unregistering, and firing callbacks.
    /// </summary>
    /// <remarks>
    /// The class that is marked with this attribute must be static and partial.
    /// The class name must end with Callback (e.g. FooCallback).
    /// In the class, a public delegate must be declared with the same name as the class, with name ending with "Delegate" (e.g. if the class was FooCallback, it must contain FooDelegate).
    /// <code>
    /// [GenerateCallbackHelper]
    /// public static partial class FooCallback
    /// {
    ///     public delegate void FooDelegate(int x, string y, readonly ref MyLargeStruct z);
    /// }
    /// </code>
    /// The generator will create the Register, Unregister, and Fire methods.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class CallbackHelperAttribute : Attribute
    {
    }

    /// <summary>
    /// A service that functions as an intermediary between components by managing interfaces, callbacks, and advisors.
    /// </summary>
    /// <remarks>
    /// A <see cref=" ComponentBroker"/> acts as a scope for the components it manages.
    /// <para>
    /// The server has a single root <see cref=" ComponentBroker"/> which acts as the "global" scope.
    /// This root <see cref=" ComponentBroker"/> is actually the <see cref="ModuleManager"/>.
    /// It registers itself as the <see cref="IComponentBroker"/> implementation.
    /// </para>
    /// Each <see cref="Arena"/> is also a <see cref="ComponentBroker"/>, with the root being the parent.
    /// </remarks>
    public class ComponentBroker : IComponentBroker
    {
        protected ComponentBroker(IComponentBroker? parent)
        {
            Parent = parent;

            if (Parent is ComponentBroker parentBroker)
            {
                parentBroker.AdvisorChanged += Parent_AdvisorChanged;
            }
        }

        public IComponentBroker? Parent { get; }

        #region Interfaces

        /// <summary>
        /// Interface registration data
        /// </summary>
        private abstract class InterfaceData(object? key)
        {
            /// <summary>
            /// The instance that is registered.
            /// </summary>
            public abstract IComponentInterface Instance { get; }

            /// <summary>
            /// An optional name for the instance. Usually <see langword="null"/>, but this allows registering multiple instances
            /// for the same same interface type, and retrieve them by name.
            /// </summary>
            public object? Key { get; } = key;

            /// <summary>
            /// A count of how many active references to the instance there are.
            /// </summary>
            public int ReferenceCount = 0;
        }

        private class InterfaceData<T>(T instance, string? name) : InterfaceData(name) where T : IComponentInterface
        {
            private readonly T _instance = instance ?? throw new ArgumentNullException(nameof(instance));

            public override IComponentInterface Instance => _instance;

            /// <summary>
            /// A token that uniquely identifies a registration.
            /// Returned when an interface is registered and later used to unregister. Only the one who registered will have it, no others can interfere.
            /// </summary>
            public InterfaceRegistrationToken<T> RegistrationToken { get; } = new ConcreteInterfaceRegistrationToken<T>();
        }

        private class ConcreteInterfaceRegistrationToken<T> : InterfaceRegistrationToken<T> where T : IComponentInterface
        {
        }

        /// <summary>
        /// For synchronizing access to <see cref="_interfaceRegistrations"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _interfaceRwLock = new();

        /// <summary>
        /// Dictionary of interface registrations where:
        /// Key is the <see cref="Type"/> of the interface.
        /// Value is a LinkedList of registrations.  
        /// <para>
        /// In the case of an interface getting registered more than once (with the same name or no name), a node earlier in the list "overrides" similar registrations that come after it.
        /// </para>
        /// </summary>
        private readonly Dictionary<Type, LinkedList<InterfaceData>> _interfaceRegistrations = [];

        public InterfaceRegistrationToken<TInterface> RegisterInterface<TInterface>(TInterface instance, string? name = null) where TInterface : class, IComponentInterface
        {
            ArgumentNullException.ThrowIfNull(instance);

            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            InterfaceData<TInterface> iData = new(instance, name);

            _interfaceRwLock.EnterWriteLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList) == false)
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

        public int UnregisterInterface<TInterface>(ref InterfaceRegistrationToken<TInterface>? token) where TInterface : class, IComponentInterface
        {
            ArgumentNullException.ThrowIfNull(token);

            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(interfaceType.Name + " is not an interface");

            _interfaceRwLock.EnterWriteLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList) == false)
                {
                    // no record of registration for the interface
                    return 0;
                }

                LinkedListNode<InterfaceData>? node = registrationList.First;
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

        public IComponentInterface? GetInterface(Type interfaceType, object? key = null)
        {
            ArgumentNullException.ThrowIfNull(interfaceType);

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("Must be an interface.", nameof(interfaceType));

            if (typeof(IComponentInterface).IsAssignableFrom(interfaceType) == false)
                throw new ArgumentException("Must be an IComponentInterface.", nameof(interfaceType));

            return GetService(interfaceType, key);
        }

        private IComponentInterface? GetService(Type interfaceType, object? key)
        {
            //
            // Try to get it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList))
                {
                    LinkedListNode<InterfaceData>? node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Key == key)
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

            return Parent?.GetInterface(interfaceType, key);
        }

        public TInterface? GetInterface<TInterface>(object? key = null) where TInterface : class, IComponentInterface
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
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList))
                {
                    LinkedListNode<InterfaceData>? node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Key == key)
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

            return Parent?.GetInterface<TInterface>(key);
        }

        public void ReleaseInterface(Type interfaceType, IComponentInterface instance, object? key = null)
        {
            ArgumentNullException.ThrowIfNull(interfaceType);

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("Must be an interface.", nameof(interfaceType));

            if (typeof(IComponentInterface).IsAssignableFrom(interfaceType) == false)
                throw new ArgumentException("Must be an IComponentInterface.", nameof(interfaceType));

            if (instance == null)
                return; // nothing to release

            //
            // Try to release it in this broker instance.
            //

            _interfaceRwLock.EnterReadLock();

            try
            {
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList))
                {
                    LinkedListNode<InterfaceData>? node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Instance == instance && node.Value.Key == key)
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

            Parent?.ReleaseInterface(interfaceType, instance, key);
        }

        public void ReleaseInterface<TInterface>(ref TInterface? instance, object? key = null) where TInterface : class, IComponentInterface
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
                if (_interfaceRegistrations.TryGetValue(interfaceType, out LinkedList<InterfaceData>? registrationList))
                {
                    LinkedListNode<InterfaceData>? node = registrationList.First;
                    while (node != null)
                    {
                        if (node.Value.Instance == instance && node.Value.Key == key)
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

            Parent?.ReleaseInterface(ref instance, key);
        }

        #endregion

        #region Callbacks

        /// <summary>
        /// For synchronizing access to the <see cref="_callbackRegistrations"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _callbackRwLock = new();

        /// <summary>
        /// The callback dictionary where: Key is the delegate type. Value is the delegate itself.
        /// </summary>
        private readonly Dictionary<Type, Delegate> _callbackRegistrations = [];

        public void RegisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate
        {
            ArgumentNullException.ThrowIfNull(handler);

            Type key = typeof(TDelegate);

            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackRegistrations.TryGetValue(key, out Delegate? d))
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

        public void UnregisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate
        {
            ArgumentNullException.ThrowIfNull(handler);

            Type key = typeof(TDelegate);

            _callbackRwLock.EnterWriteLock();

            try
            {
                if (_callbackRegistrations.TryGetValue(key, out Delegate? d) == false)
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

        public TDelegate? GetCallback<TDelegate>() where TDelegate : Delegate
        {
            Type key = typeof(TDelegate);

            _callbackRwLock.EnterReadLock();

            try
            {
                if (!_callbackRegistrations.TryGetValue(key, out Delegate? d))
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

        #region Advisors

        private class ConcreteAdvisorRegistrationToken<T>(ComponentBroker broker, T instance) : AdvisorRegistrationToken<T> where T : IComponentAdvisor
        {
            /// <summary>
            /// The broker that created the token.
            /// </summary>
            public readonly ComponentBroker Broker = broker ?? throw new ArgumentNullException(nameof(broker));

            /// <summary>
            /// The advisor that was registered.
            /// </summary>
            public readonly T Instance = instance ?? throw new ArgumentNullException(nameof(instance));

            /// <summary>
            /// Whether the token is still active. A token can be used to unregister a previously registered advisor once and only once.
            /// </summary>
            public bool IsActive { get; private set; } = true;

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
            public abstract void RefreshCombined(IComponentBroker? parent);
        }

        private class AdvisorData<TAdvisor> : AdvisorData where TAdvisor : IComponentAdvisor
        {
            /// <summary>
            /// The registered advisors.
            /// </summary>
            public ImmutableArray<TAdvisor> Registered { get; private set; } = [];

            /// <summary>
            /// <see cref="Registered"/> combined with those from parent.
            /// </summary>
            public ImmutableArray<TAdvisor> Advisors { get; private set; } = [];

            public void AddAndRecombine(TAdvisor toAdd, IComponentBroker? parent)
            {
                if (toAdd != null)
                {
                    Registered = Registered.Add(toAdd);
                }

                RefreshCombined(parent);
            }

            public bool RemoveAndRecombine(TAdvisor toRemove, IComponentBroker? parent)
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

            public override void RefreshCombined(IComponentBroker? parent)
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

        private readonly Dictionary<Type, AdvisorData> _advisorDictionary = [];
        private readonly ReaderWriterLockSlim _advisorLock = new();

        private event Action<Type>? AdvisorChanged;

        private void Parent_AdvisorChanged(Type advisorType)
        {
            _advisorLock.EnterWriteLock();

            try
            {
                if (_advisorDictionary.TryGetValue(advisorType, out AdvisorData? advisorData))
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

        public AdvisorRegistrationToken<TAdvisor> RegisterAdvisor<TAdvisor>(TAdvisor advisor) where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            ArgumentNullException.ThrowIfNull(advisor);

            _advisorLock.EnterWriteLock();

            try
            {
                if (!_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData? advisorData)
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

        public bool UnregisterAdvisor<TAdvisor>(ref AdvisorRegistrationToken<TAdvisor>? token) where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            ArgumentNullException.ThrowIfNull(token);

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

                if (!_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData? advisorData)
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

        public ImmutableArray<TAdvisor> GetAdvisors<TAdvisor>() where TAdvisor : IComponentAdvisor
        {
            if (!typeof(TAdvisor).IsInterface)
                throw new Exception("The type parameter must be an interface.");

            _advisorLock.EnterReadLock();

            try
            {
                if (_advisorDictionary.TryGetValue(typeof(TAdvisor), out AdvisorData? advisorData)
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
                return [];
            }
        }

        #endregion
    }
}
