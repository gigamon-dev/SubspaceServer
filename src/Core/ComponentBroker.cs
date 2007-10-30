using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
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
        private Dictionary<Type, IModuleInterface> _interfaceLookup = new Dictionary<Type, IModuleInterface>();
        private Dictionary<Type, int> _interfaceReferenceLookup = new Dictionary<Type, int>();

        public void RegisterInterface<TInterface>(TInterface implementor) where TInterface : IModuleInterface
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

        public int UnregisterInterface<TInterface>() where TInterface : IModuleInterface
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

        public virtual IModuleInterface GetInterface(Type interfaceType)
        {
            if (interfaceType == null)
                throw new ArgumentNullException("interfaceType");

            if (interfaceType.IsInterface == false)
                throw new ArgumentException("type must be an interface", "interfaceType");

            lock (_interfaceLockObj)
            {
                IModuleInterface theInterface;
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

        public virtual TInterface GetInterface<TInterface>() where TInterface : class, IModuleInterface
        {
            Type interfaceType = typeof(TInterface);
            if (interfaceType.IsInterface == false)
                throw new Exception(string.Format("type is not an interface [{0}]", interfaceType.FullName));

            lock (_interfaceLockObj)
            {
                IModuleInterface theInterface;
                if (_interfaceLookup.TryGetValue(interfaceType, out theInterface) == false)
                    return null;

                TInterface theConcreteInterface = theInterface as TInterface;
                if(theConcreteInterface == null)
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

        private object _callbackLockObj = new object();
        private Dictionary<string, Delegate> _callbackLookup = new Dictionary<string, Delegate>();

        public void RegisterCallback<TDelegate>(string callbackIdentifier, Delegate handler) where TDelegate : class
        {
#if DEBUG
            // only do this nice type check in debug mode
            TDelegate test = handler as TDelegate;
            if (test == null)
            {
                throw new ArgumentException("wrong type of delegate", "handler");
            }
#endif

            Delegate d;

            lock (_callbackLockObj)
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
                    _callbackLookup.Add(callbackIdentifier, handler);
                }
                else
                {
                    _callbackLookup[callbackIdentifier] = Delegate.Combine(d, handler);
                }
            }
        }

        /// <summary>
        /// Unregisters a handler
        /// </summary>
        /// <param name="callbackIdentifier"></param>
        /// <param name="handler"></param>
        public void UnregisterCallback(string callbackIdentifier, Delegate handler)
        {
            Delegate d;

            lock (_callbackLockObj)
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
        }

        /// <summary>
        /// To invoke all of the registered callbacks
        /// </summary>
        /// <param name="callbackIdentifier">unique identifier of the callback</param>
        /// <param name="args">arguments to pass (be aware that value types are boxed)</param>
        public virtual void DoCallbacks(string callbackIdentifier, params object[] args)
        {
            Delegate d;

            lock (_callbackLockObj)
            {
                if (_callbackLookup.TryGetValue(callbackIdentifier, out d) == false)
                {
#if DEBUG
                    Console.WriteLine("attempt to invoke a callback that has no handlers [{0}]", callbackIdentifier);
#endif
                    return;
                }
            }

            d.DynamicInvoke(args);
        }

        /// <summary>
        /// Retrieves a callback by ID
        /// </summary>
        /// <typeparam name="TDelegate">the type of delegate looking for</typeparam>
        /// <param name="callbackIdentifier">unique identifier of the callback</param>
        /// <returns>the multicast delegate, null if not found or type does not match</returns>
        public virtual TDelegate LookupCallback<TDelegate>(string callbackIdentifier) where TDelegate : class
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

        #endregion
    }
}
