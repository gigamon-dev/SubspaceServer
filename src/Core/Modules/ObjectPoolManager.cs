using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for managing and tracking object pool usage.
    /// </summary>
    [CoreModuleInfo]
    public class ObjectPoolManager : IModule, IObjectPoolManager
    {
        private InterfaceRegistrationToken<IObjectPoolManager>? _iObjectPoolManagerToken;

        private readonly ConcurrentDictionary<IPool, IPool> _poolDictionary = new();

        private readonly DefaultObjectPoolProvider _provider;
        private readonly ObjectPool<HashSet<Player>> _playerHashSetPool;
        private readonly ObjectPool<HashSet<Arena>> _arenaHashSetPool;
        private readonly ObjectPool<HashSet<string>> _nameHashSetPool;
        private readonly ObjectPool<StringBuilder> _stringBuilderPool;
        private readonly ObjectPool<Crc32> _crc32Pool;

        public ObjectPoolManager()
        {
            _provider = new DefaultObjectPoolProvider();
            _playerHashSetPool = _provider.Create(new HashSetPooledObjectPolicy<Player>() { InitialCapacity = Constants.TargetPlayerCount });
            _arenaHashSetPool = _provider.Create(new HashSetPooledObjectPolicy<Arena>() { InitialCapacity = Constants.TargetArenaCount });
            _nameHashSetPool = _provider.Create(new HashSetPooledObjectPolicy<string>() { InitialCapacity = Constants.TargetPlayerCount, EqualityComparer = StringComparer.OrdinalIgnoreCase });
            _stringBuilderPool = _provider.CreateStringBuilderPool(512, 4 * 1024);
            _crc32Pool = _provider.Create(new Crc32PooledObjectPolicy());
        }

        bool IModule.Load(IComponentBroker broker)
        {
            _iObjectPoolManagerToken = broker.RegisterInterface<IObjectPoolManager>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
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

        ObjectPool<HashSet<Player>> IObjectPoolManager.PlayerSetPool => _playerHashSetPool;

        ObjectPool<HashSet<Arena>> IObjectPoolManager.ArenaSetPool => _arenaHashSetPool;

        ObjectPool<HashSet<string>> IObjectPoolManager.NameHashSetPool => _nameHashSetPool;

        ObjectPool<StringBuilder> IObjectPoolManager.StringBuilderPool => _stringBuilderPool;

        ObjectPool<Crc32> IObjectPoolManager.Crc32Pool => _crc32Pool;

        #endregion

        #region PooledObjectPolicy classes

        private class Crc32PooledObjectPolicy : IPooledObjectPolicy<Crc32>
        {
            public Crc32 Create()
            {
                return new Crc32();
            }

            public bool Return(Crc32 obj)
            {
                if (obj is null)
                    return false;

                obj.Reset();
                return true;
            }
        }

        #endregion
    }
}
