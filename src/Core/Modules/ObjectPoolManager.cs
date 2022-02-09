using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
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
    public class ObjectPoolManager : IModule, IObjectPoolManager
    {
        private InterfaceRegistrationToken _iObjectPoolManagerToken;

        private readonly ConcurrentDictionary<IPool, IPool> _poolDictionary = new();

        private DefaultObjectPoolProvider _provider;
        private ObjectPool<HashSet<Player>> _playerHashSetPool;
        private ObjectPool<StringBuilder> _stringBuilderPool;
        private ObjectPool<IPEndPoint> _ipEndPointPool;
        private ObjectPool<List<Brick>> _brickListPool;
        private ObjectPool<List<BrickData>> _brickDataListPool;

        public bool Load(ComponentBroker broker)
        {
            _iObjectPoolManagerToken = broker.RegisterInterface<IObjectPoolManager>(this);

            _provider = new DefaultObjectPoolProvider();
            _playerHashSetPool = _provider.Create(new PlayerHashSetPooledObjectPolicy());
            _stringBuilderPool = _provider.CreateStringBuilderPool(512, 4 * 1024);
            _ipEndPointPool = _provider.Create(new IPEndPointPooledObjectPolicy());
            _brickListPool = _provider.Create(new BrickListPooledObjectPolicy());
            _brickDataListPool = _provider.Create(new BrickDataListPooledObjectPolicy());

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
            TryAddTrackedPool(pool);
            return pool;
        }

        T IObjectPoolManager.Get<T>()
        {
            var pool = Pool<T>.Default;
            TryAddTrackedPool(pool);
            return pool.Get();
        }

        IEnumerable<IPool> IObjectPoolManager.Pools => _poolDictionary.Values;

        bool IObjectPoolManager.TryAddTracked(IPool pool) => TryAddTrackedPool(pool);

        private bool TryAddTrackedPool(IPool pool) => _poolDictionary.TryAdd(pool, pool);

        bool IObjectPoolManager.TryRemoveTracked(IPool pool) => _poolDictionary.TryRemove(pool, out _);

        ObjectPool <HashSet<Player>> IObjectPoolManager.PlayerSetPool => _playerHashSetPool;

        ObjectPool<StringBuilder> IObjectPoolManager.StringBuilderPool => _stringBuilderPool;

        ObjectPool<IPEndPoint> IObjectPoolManager.IPEndPointPool => _ipEndPointPool;

        ObjectPool<List<Brick>> IObjectPoolManager.BrickListPool => _brickListPool;

        ObjectPool<List<BrickData>> IObjectPoolManager.BrickDataListPool => _brickDataListPool;

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
                if (obj == null)
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
                if (obj == null)
                    return false;

                obj.Address = IPAddress.Any;
                obj.Port = 0;
                return true;
            }
        }

        private class BrickListPooledObjectPolicy : PooledObjectPolicy<List<Brick>>
        {
            public int InitialCapacity { get; set; } = 8;

            public override List<Brick> Create()
            {
                return new List<Brick>(InitialCapacity);
            }

            public override bool Return(List<Brick> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class BrickDataListPooledObjectPolicy : PooledObjectPolicy<List<BrickData>>
        {
            public int InitialCapacity { get; set; } = 8;

            public override List<BrickData> Create()
            {
                return new List<BrickData>(InitialCapacity);
            }

            public override bool Return(List<BrickData> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        #endregion
    }
}
