using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.Core
{
    public delegate void ComponentCallbackDelegate<T1>(T1 t1);
    public delegate void ComponentCallbackDelegate<T1, T2>(T1 t1, T2 t2);
    public delegate void ComponentCallbackDelegate<T1, T2, T3>(T1 t1, T2 t2, T3 t3);
    public delegate void ComponentCallbackDelegate<T1, T2, T3, T4>(T1 t1, T2 t2, T3 t3, T4 t4);
    public delegate void ComponentCallbackDelegate<T1, T2, T3, T4, T5>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5);
    public delegate void ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6);
    public delegate void ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7);

    /// <summary>
    /// Base interface for interfaces that are registerable with the ComponentBroker
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
        protected ComponentBroker()
        {
        }

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
        /// to protect the _callbackLookup & the delegates that it contains
        /// read lock when doing callbacks, 
        /// write lock when registering/unregistering with a callback
        /// </summary>
        private ReaderWriterLock _callbackRwLock = new ReaderWriterLock();
        private Dictionary<string, Delegate> _callbackLookup = new Dictionary<string, Delegate>();

        /// <summary>
        /// it is recommended to use generic RegisterCallback methods instead, keeping this exposed just in case it's needed
        /// </summary>
        /// <typeparam name="TDelegate"></typeparam>
        /// <param name="callbackIdentifier"></param>
        /// <param name="handler"></param>
        public void RegisterRawCallback<TDelegate>(string callbackIdentifier, TDelegate handler) where TDelegate : class
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            if (handler == null)
                throw new ArgumentNullException("handler");

            Delegate dToAdd = handler as Delegate;
            if (dToAdd == null)
                throw new ArgumentException("must be a Delegate", "handler");

            Delegate d;

            _callbackRwLock.AcquireWriterLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    _callbackLookup.Add(callbackIdentifier, dToAdd);
                }
                else
                {
                    _callbackLookup[callbackIdentifier] = Delegate.Combine(d, dToAdd);
                }
            }
            finally
            {
                _callbackRwLock.ReleaseWriterLock();
            }
        }

        /// <summary>
        /// Unregisters a handler
        /// </summary>
        /// <param name="callbackIdentifier"></param>
        /// <param name="handler"></param>
        public void UnregisterCallback(string callbackIdentifier, Delegate handler)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            if (handler == null)
                throw new ArgumentNullException("handler");

            Delegate d;

            _callbackRwLock.AcquireWriterLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }
                else
                {
                    d = Delegate.Remove(d, handler);
                    if (d == null)
                        _callbackLookup.Remove(callbackIdentifier);
                    else
                        _callbackLookup[callbackIdentifier] = d;
                }
            }
            finally
            {
                _callbackRwLock.ReleaseWriterLock();
            }
        }

        /// <summary>
        /// To invoke all of the registered callbacks
        /// </summary>
        /// <param name="callbackIdentifier">unique identifier of the callback</param>
        /// <param name="args">arguments to pass (be aware that value types are boxed)</param>
        public virtual void DoCallbacks(string callbackIdentifier, params object[] args)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
#if DEBUG
                    Console.WriteLine("attempt to invoke a callback that has no handlers [{0}]", callbackIdentifier);
#endif
                    return;
                }

                d.DynamicInvoke(args);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }
        /*
                /// <summary>
                /// Retrieves a callback by ID
                /// </summary>
                /// <typeparam name="TDelegate">the type of delegate looking for</typeparam>
                /// <param name="callbackIdentifier">unique identifier of the callback</param>
                /// <returns>the multicast delegate, null if not found or type does not match</returns>
                protected virtual TDelegate LookupCallback<TDelegate>(string callbackIdentifier) where TDelegate : class
                {
                    Delegate d;

                    lock (_callbackLockObj)
                    {
                        if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                        {
                            return null;
                        }
                    }

                    return d as TDelegate;
                }
        */
        #endregion

        #region Generic Callback Methods

        public void RegisterCallback<T1>(string callbackIdentifier, ComponentCallbackDelegate<T1> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2, T3>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2, T3>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2, T3, T4>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2, T3, T4>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2, T3, T4, T5>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2, T3, T4, T5>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2, T3, T4, T5, T6>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6>>(callbackIdentifier, handler);
        }

        public void RegisterCallback<T1, T2, T3, T4, T5, T6, T7>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7> handler)
        {
            RegisterRawCallback<ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7>>(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1>(string callbackIdentifier, ComponentCallbackDelegate<T1> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2, T3>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2, T3, T4>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2, T3, T4, T5>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2, T3, T4, T5, T6>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public void UnRegisterCallback<T1, T2, T3, T4, T5, T6, T7>(string callbackIdentifier, ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7> handler)
        {
            UnregisterCallback(callbackIdentifier, handler);
        }

        public virtual void DoCallback<T1>(string callbackIdentifier, T1 t1)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1> ccd = d as ComponentCallbackDelegate<T1>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2>(string callbackIdentifier, T1 t1, T2 t2)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2> ccd = d as ComponentCallbackDelegate<T1, T2>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2, T3>(string callbackIdentifier, T1 t1, T2 t2, T3 t3)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2, T3> ccd = d as ComponentCallbackDelegate<T1, T2, T3>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2, t3);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2, T3, T4>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2, T3, T4> ccd = d as ComponentCallbackDelegate<T1, T2, T3, T4>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2, t3, t4);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2, T3, T4, T5>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2, T3, T4, T5> ccd = d as ComponentCallbackDelegate<T1, T2, T3, T4, T5>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2, t3, t4, t5);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2, T3, T4, T5, T6>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6> ccd = d as ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2, t3, t4, t5, t6);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        public virtual void DoCallback<T1, T2, T3, T4, T5, T6, T7>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7)
        {
            if (callbackIdentifier == null) // allowing empty string
                throw new ArgumentNullException("callbackIdentifier");

            Delegate d;

            _callbackRwLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    return;
                }

                ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7> ccd = d as ComponentCallbackDelegate<T1, T2, T3, T4, T5, T6, T7>;
                if (ccd == null)
                    throw new Exception("callback delegate type does not match");

                ccd(t1, t2, t3, t4, t5, t6, t7);
            }
            finally
            {
                _callbackRwLock.ReleaseReaderLock();
            }
        }

        #endregion
    }
}
