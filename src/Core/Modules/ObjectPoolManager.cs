using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for managing and tracking object pool usage.
    /// </summary>
    public class ObjectPoolManager : IModule, IObjectPoolManager
    {
        private InterfaceRegistrationToken _iObjectPoolManagerToken;

        private readonly ConcurrentDictionary<Type, IPool> _poolDictionary = new();

        private DefaultObjectPoolProvider _provider;
        private ObjectPool<HashSet<Player>> _playerHashSetPool;
        private ObjectPool<StringBuilder> _stringBuilderPool;

        public bool Load(ComponentBroker broker)
        {
            _iObjectPoolManagerToken = broker.RegisterInterface<IObjectPoolManager>(this);

            _provider = new DefaultObjectPoolProvider();
            _playerHashSetPool = _provider.Create(new PlayerHashSetPooledObjectPolicy());
            _stringBuilderPool = _provider.CreateStringBuilderPool(512, 4 * 1024);

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

        ObjectPool<HashSet<Player>> IObjectPoolManager.PlayerSetPool => _playerHashSetPool;

        ObjectPool<StringBuilder> IObjectPoolManager.StringBuilderPool => _stringBuilderPool;

        #endregion

        private class PlayerHashSetPooledObjectPolicy : PooledObjectPolicy<HashSet<Player>>
        {
            public int InitialCapacity { get; set; } = 256;

            public override HashSet<Player> Create()
            {
                return new HashSet<Player>(InitialCapacity);
            }

            public override bool Return(HashSet<Player> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }
    }
}
