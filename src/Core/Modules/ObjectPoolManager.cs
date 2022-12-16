using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for managing and tracking object pool usage.
    /// </summary>
    [CoreModuleInfo]
    public class ObjectPoolManager : IModule, IObjectPoolManager
    {
        private InterfaceRegistrationToken<IObjectPoolManager> _iObjectPoolManagerToken;

        private readonly ConcurrentDictionary<IPool, IPool> _poolDictionary = new();

        private DefaultObjectPoolProvider _provider;
        private ObjectPool<HashSet<Player>> _playerHashSetPool;
        private ObjectPool<HashSet<Arena>> _arenaHashSetPool;
        private ObjectPool<StringBuilder> _stringBuilderPool;
        private ObjectPool<IPEndPoint> _ipEndPointPool;
        

        public bool Load(ComponentBroker broker)
        {
            _iObjectPoolManagerToken = broker.RegisterInterface<IObjectPoolManager>(this);

            _provider = new DefaultObjectPoolProvider();
            _playerHashSetPool = _provider.Create(new PlayerHashSetPooledObjectPolicy());
            _arenaHashSetPool = _provider.Create(new ArenaHashSetPooledObjectPolicy());
            _stringBuilderPool = _provider.CreateStringBuilderPool(512, 4 * 1024);
            _ipEndPointPool = _provider.Create(new IPEndPointPooledObjectPolicy());

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iObjectPoolManagerToken) != 0)
                return false;

            return true;
        }

        #region IObjectPoolManager Members

        Pool<T> IObjectPoolManager.GetPool<T>()
        {
            var pool = Pool<T>.Default;
            TryAddTrackedPool(pool);
            return pool;
        }

        IEnumerable<IPool> IObjectPoolManager.Pools => _poolDictionary.Values;

        bool IObjectPoolManager.TryAddTracked(IPool pool) => TryAddTrackedPool(pool);

        private bool TryAddTrackedPool(IPool pool) => _poolDictionary.TryAdd(pool, pool);

        bool IObjectPoolManager.TryRemoveTracked(IPool pool) => _poolDictionary.TryRemove(pool, out _);

        ObjectPool <HashSet<Player>> IObjectPoolManager.PlayerSetPool => _playerHashSetPool;

        ObjectPool<HashSet<Arena>> IObjectPoolManager.ArenaSetPool => _arenaHashSetPool;

        ObjectPool<StringBuilder> IObjectPoolManager.StringBuilderPool => _stringBuilderPool;

        ObjectPool<IPEndPoint> IObjectPoolManager.IPEndPointPool => _ipEndPointPool;

        #endregion

        #region PooledObjectPolicy classes

        private class PlayerHashSetPooledObjectPolicy : PooledObjectPolicy<HashSet<Player>>
        {
            public int InitialCapacity { get; set; } = 256;

            public override HashSet<Player> Create()
            {
                return new HashSet<Player>(InitialCapacity);
            }

            public override bool Return(HashSet<Player> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class ArenaHashSetPooledObjectPolicy : PooledObjectPolicy<HashSet<Arena>>
        {
            public int InitialCapacity { get; set; } = 32;

            public override HashSet<Arena> Create()
            {
                return new HashSet<Arena>(InitialCapacity);
            }

            public override bool Return(HashSet<Arena> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class IPEndPointPooledObjectPolicy : PooledObjectPolicy<IPEndPoint>
        {
            public override IPEndPoint Create()
            {
                return new IPEndPoint(IPAddress.Any, 0);
            }

            public override bool Return(IPEndPoint obj)
            {
                if (obj is null)
                    return false;

                obj.Address = IPAddress.Any;
                obj.Port = 0;
                return true;
            }
        }

        #endregion
    }
}
