using SS.Core.ComponentInterfaces;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for that provides a bandwidth limiters that do not impose a limit.
    /// </summary>
    [CoreModuleInfo]
    public sealed class BandwidthNoLimit : IModule, IBandwidthLimiterProvider
    {
        public const string InterfaceIdentifier = nameof(BandwidthNoLimit);

        private InterfaceRegistrationToken<IBandwidthLimiterProvider>? _iBandwidthLimiterProviderNamedToken;
        private InterfaceRegistrationToken<IBandwidthLimiterProvider>? _iBandwidthLimiterProviderToken;

        // This limiter does not keep any state; it is immutable. Therefore, we just use one instance for all.
        private readonly NoLimitBandwidthLimiter _singleton = new();

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            _iBandwidthLimiterProviderNamedToken = broker.RegisterInterface<IBandwidthLimiterProvider>(this, InterfaceIdentifier);
            _iBandwidthLimiterProviderToken = broker.RegisterInterface<IBandwidthLimiterProvider>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iBandwidthLimiterProviderNamedToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iBandwidthLimiterProviderToken) != 0)
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

        /// <summary>
        /// An <see cref="IBandwidthLimiter"/> that does not enforce any limit.
        /// </summary>
        private class NoLimitBandwidthLimiter : IBandwidthLimiter
        {
            #region IBandwidthLimiter Members

            public void Iter(long asOf)
            {
            }

            public bool Check(int bytes, BandwidthPriority priority, bool modify)
            {
                return true;
            }

            public void AdjustForAck()
            {
            }

            public void AdjustForRetry()
            {
            }

            public int GetSendWindowSize()
            {
                return 30;
            }

            public void GetInfo(StringBuilder sb)
            {
                sb?.Append("(no limit)");
            }

            #endregion
        }
    }
}
