using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for managing and tracking object pool usage.
    /// </summary>
    public class ObjectPoolManager : IModule, IObjectPoolManager
    {
        private InterfaceRegistrationToken _iObjectPoolManagerToken;

        private readonly ConcurrentDictionary<Type, IPool> _poolDictionary = new();

        public bool Load(ComponentBroker broker)
        {
            _iObjectPoolManagerToken = broker.RegisterInterface<IObjectPoolManager>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IObjectPoolManager>(ref _iObjectPoolManagerToken);
            return true;
        }

        #region IObjectPoolManager Members

        IPool<T> IObjectPoolManager.GetPool<T>()
        {
            var pool = Pool<T>.Default;
            _poolDictionary.TryAdd(typeof(T), pool);
            return pool;
        }

        T IObjectPoolManager.Get<T>()
        {
            var pool = Pool<T>.Default;
            _poolDictionary.TryAdd(typeof(T), pool);
            return pool.Get();
        }

        IEnumerable<IPool> IObjectPoolManager.Pools => _poolDictionary.Values;

        #endregion
    }
}
