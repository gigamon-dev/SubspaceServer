using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules
{
    /// <summary>
    /// these are the available classes/priorities of traffic
    /// </summary>
    public enum BandwidthPriorities
    {
        UnreliableLow = 0, 
        Unreliable, 
        UnreliableHigh, 
        Reliable, 
        Ack, 
    }

    [CoreModuleInfo]
    public class BandwidthNoLimit : IModule, IBandwidthLimit
    {
        private InterfaceRegistrationToken _iBandwidthLimitToken;

        private class BWLimit : IBWLimit
        {
            #region IBWLimit Members

            public void Iter(DateTime now)
            {
            }

            public bool Check(int bytes, int pri)
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

            public string GetInfo()
            {
                return "(no limit)";
            }

            #endregion
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _iBandwidthLimitToken = broker.RegisterInterface<IBandwidthLimit>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IBandwidthLimit>(ref _iBandwidthLimitToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IBandwidthLimit Members

        IBWLimit IBandwidthLimit.New()
        {
            return new BWLimit();
        }

        #endregion
    }

    // TODO: i dont understand what ASSS is doing with the calculations with millseconds, ticks, etc
    [CoreModuleInfo]
    public class DefaultBandwithLimit : IModule, IBandwidthLimit
    {
        private IConfigManager config;
        private InterfaceRegistrationToken iBandwidthLimitToken;

        private class BWLimit : IBWLimit
        {
            #region Configuration

            /// <summary>
            /// low bandwidth limit
            /// </summary>
            public static int LimitLow;

            /// <summary>
            /// high bandwidth limit
            /// </summary>
            public static int LimitHigh;

            /// <summary>
            /// initial bandwidth limit
            /// </summary>
            public static int LimitInitial;

            /// <summary>
            /// this array represents the percentage of traffic that is allowed to be at or lower than each priority level
            /// </summary>
            public static int[] PriorityLimits = new int[(int)Enum.GetValues<BandwidthPriorities>().Max() + 1];

            /// <summary>
            /// we need to know how many packets the client is able to buffer
            /// </summary>
            public static int ClientCanBuffer;

            public static int LimitScale;
            public static int MaxAvail;
            public static bool UseHitLimit;

            #endregion

            private int _limit;
            private int[] _avail = new int[(int)Enum.GetValues<BandwidthPriorities>().Max() + 1];
            private int _maxavail;
            private bool _hitlimit;
            private DateTime _sincetime;

            public BWLimit()
            {
                _limit = LimitInitial;
                _maxavail = MaxAvail;
                _hitlimit = false;
                _sincetime = DateTime.UtcNow;
            }

            #region IBWLimit Members

            public void Iter(DateTime now)
            {
                //const int granularity = 8;
                //int pri = 0;
                //int slices = 0;

                //(now - _sincetime)
            }

            public bool Check(int bytes, int pri)
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

            public string GetInfo()
            {
                return "(no limit)";
            }

            #endregion
        }

        #region IModule Members

        public bool Load(ComponentBroker mm, IConfigManager configManager)
        {
            config = configManager ?? throw new ArgumentNullException(nameof(configManager));

            BWLimit.LimitLow = config.GetInt(config.Global, "Net", "LimitMinimum", 2500);
            BWLimit.LimitHigh = config.GetInt(config.Global, "Net", "LimitMaximum", 102400);
            BWLimit.LimitInitial = config.GetInt(config.Global, "Net", "LimitInitial", 5000);
            BWLimit.ClientCanBuffer = config.GetInt(config.Global, "Net", "SendAtOnce", 255);
            BWLimit.LimitScale = config.GetInt(config.Global, "Net", "LimitScale", Constants.MaxPacket * 1);
            BWLimit.MaxAvail = config.GetInt(config.Global, "Net", "Burst", Constants.MaxPacket * 4);
            BWLimit.UseHitLimit = config.GetInt(config.Global, "Net", "UseHitLimit", 0) != 0;

            BWLimit.PriorityLimits[0] = config.GetInt(config.Global, "Net", "PriLimit0", 20); // low pri unrel
            BWLimit.PriorityLimits[1] = config.GetInt(config.Global, "Net", "PriLimit1", 40); // reg pri unrel
            BWLimit.PriorityLimits[2] = config.GetInt(config.Global, "Net", "PriLimit2", 20); // high pri unrel
            BWLimit.PriorityLimits[3] = config.GetInt(config.Global, "Net", "PriLimit3", 15); // rel
            BWLimit.PriorityLimits[4] = config.GetInt(config.Global, "Net", "PriLimit4", 5);  // ack

            iBandwidthLimitToken = mm.RegisterInterface<IBandwidthLimit>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IBandwidthLimit>(ref iBandwidthLimitToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IBandwidthLimit Members

        IBWLimit IBandwidthLimit.New()
        {
            return new BWLimit();
        }

        #endregion
    }
}
