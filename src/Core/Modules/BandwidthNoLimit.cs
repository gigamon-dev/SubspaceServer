using SS.Core.ComponentInterfaces;
using System;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for that provides a bandwidth limiters that do not impose a limit.
    /// </summary>
    [CoreModuleInfo]
    public class BandwidthNoLimit : IModule, IBandwidthLimiterProvider
    {
        private InterfaceRegistrationToken _iBandwidthLimitToken;

        // This limiter does not keep any state; it is immutable. Therefore, we just use one instance for all.
        private readonly NoLimitBandwidthLimiter _singleton = new();

        /// <summary>
        /// An <see cref="IBandwidthLimiter"/> that does not enforce any limit.
        /// </summary>
        private class NoLimitBandwidthLimiter : IBandwidthLimiter
        {
            #region IBandwidthLimiter Members

            public void Iter(DateTime now)
            {
            }

            public bool Check(int bytes, BandwidthPriority priority)
            {
                return true;
            }

            public void AdjustForAck()
            {
            }

            public void AdjustForRetry()
            {
            }

            public int GetCanBufferPackets()
            {
                return 30;
            }

            public void GetInfo(StringBuilder sb)
            {
                sb?.Append("(no limit)");
            }

            #endregion
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _iBandwidthLimitToken = broker.RegisterInterface<IBandwidthLimiterProvider>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IBandwidthLimiterProvider>(ref _iBandwidthLimitToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IBandwidthLimiterProvider Members

        IBandwidthLimiter IBandwidthLimiterProvider.New()
        {
            return _singleton;
        }

        void IBandwidthLimiterProvider.Free(IBandwidthLimiter limiter)
        {
            // no-op
        }

        #endregion
    }
}
