using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that implements the <see cref="IPrng"/> interface to wrap pseudo-random number generation functionality.
    /// </summary>
    [CoreModuleInfo]
    public class Prng : IModule, IPrng
    {
        private InterfaceRegistrationToken<IPrng> _iPrngToken;

        private readonly Random _random = Random.Shared; // thread-safe instance

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
            _random.NextBytes(data);
        }

        uint IPrng.Get32()
        {
            return (uint)_random.Next();
        }

        int IPrng.Number(int start, int end)
        {
            return _random.Next(start, end + 1);
        }

        int IPrng.Rand()
        {
            return _random.Next(Constants.RandMax + 1);
        }

        double IPrng.Uniform()
        {
            return _random.NextDouble();
        }

        void IPrng.Shuffle<T>(Span<T> values)
        {
            // TODO: with .NET 8 replace with _random.Shuffle(values);

            // Fisher–Yates shuffle
            for (int i = values.Length - 1; i > 0; i--)
            {
                int randomIndex = ((IPrng)this).Number(0, i);
                if (randomIndex != i)
                {
                    // swap
                    (values[randomIndex], values[i]) = (values[i], values[randomIndex]);
                }
            }
        }

        void IPrng.Shuffle<T>(IList<T> values)
        {
            if (values is null)
                return;

            // Fisher–Yates shuffle
            for (int i = values.Count - 1; i > 0; i--)
            {
                int randomIndex = ((IPrng)this).Number(0, i);
                if (randomIndex != i)
                {
                    // swap
                    (values[randomIndex], values[i]) = (values[i], values[randomIndex]);
                }
            }
        }
    }
}
