using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that implements the <see cref="IPrng"/> interface to wrap pseudo-random number generation functionality.
    /// </summary>
    public class Prng : IModule, IPrng
    {
        private InterfaceRegistrationToken<IPrng> _iPrngToken;

        private readonly object _randomLock = new();
        private readonly Random _random = new();

        private readonly object _rngLock = new();
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        public bool Load(ComponentBroker broker)
        {
            _iPrngToken = broker.RegisterInterface<IPrng>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPrngToken) != 0)
                return false;

            return true;
        }

        void IPrng.GoodFillBuffer(Span<byte> data)
        {
            lock (_rngLock)
            {
                _rng.GetBytes(data);
            }
        }

        void IPrng.FillBuffer(Span<byte> data)
        {
            lock (_randomLock)
            {
                _random.NextBytes(data);
            }
        }

        uint IPrng.Get32()
        {
            lock (_randomLock)
            {
                return (uint)_random.Next();
            }
        }

        int IPrng.Number(int start, int end)
        {
            lock (_randomLock)
            {
                return _random.Next(start, end + 1);
            }
        }

        int IPrng.Rand()
        {
            lock (_randomLock)
            {
                return _random.Next(Constants.RandMax + 1);
            }
        }

        double IPrng.Uniform()
        {
            lock (_randomLock)
            {
                return _random.NextDouble();
            }
        }
    }
}
