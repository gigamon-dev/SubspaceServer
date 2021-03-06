﻿using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SS.Core.Modules
{
    public class Prng : IModule, IPrng
    {
        private InterfaceRegistrationToken iPrngToken;

        private readonly object randomLock = new object();
        private readonly Random random = new Random();

        private readonly object rngLock = new object();
        private readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();

        public bool Load(ComponentBroker broker)
        {
            iPrngToken = broker.RegisterInterface<IPrng>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IPrng>(ref iPrngToken);
            return true;
        }

        void IPrng.GoodFillBuffer(Span<byte> data)
        {
            lock (rngLock)
            {
                rng.GetBytes(data);
            }
        }

        void IPrng.FillBuffer(Span<byte> data)
        {
            lock (randomLock)
            {
                random.NextBytes(data);
            }
        }

        uint IPrng.Get32()
        {
            lock (randomLock)
            {
                return (uint)random.Next();
            }
        }

        int IPrng.Number(int start, int end)
        {
            lock (randomLock)
            {
                return random.Next(start, end + 1);
            }
        }

        int IPrng.Rand()
        {
            lock (randomLock)
            {
                return random.Next(Constants.RandMax + 1);
            }
        }

        double IPrng.Uniform()
        {
            lock (randomLock)
            {
                return random.NextDouble();
            }
        }
    }
}
