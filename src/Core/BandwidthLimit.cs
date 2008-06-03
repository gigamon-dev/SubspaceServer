using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// these are the available classes/priorities of traffic
    /// </summary>
    public enum BandwidthPriorities
    {
        UnreiableLow = 0, 
        Unreliable, 
        UnreliableHigh, 
        Reliable, 
        Ack, 
        NumPriorities
    }

    public interface IBWLimit
    {
        /// <summary>
        /// adjust the current idea of how many bytes have been sent
        /// recently. call once in a while. now is in millis, not ticks
        /// </summary>
        /// <param name="ms"></param>
        void Iter(DateTime now);

        /// <summary>
        /// checks if <paramref name="bytes"/> bytes at priority <paramref name="pri"/> can be sent according to
        /// the current limit and sent counters. if they can be sent, modifies bw
        /// and returns true, otherwise returns false
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="pri"></param>
        /// <returns></returns>
        bool Check(int bytes, int pri);

        void AdjustForAck();
        void AdjustForRetry();

        /// <summary>
        /// to get how many reliable packets can be buffered
        /// </summary>
        /// <returns></returns>
        int GetCanBufferPackets();
        string GetInfo();
    }

    public interface IBandwidthLimit : IComponentInterface
    {
        /// <summary>
        /// To get an object which handles bandwidth limiting for a single connection.
        /// </summary>
        /// <returns>a bandwidth limiter</returns>
        IBWLimit New();
    }

    public class BandwidthNoLimit : IModule, IBandwidthLimit
    {
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

        public Type[] InterfaceDependencies
        {
            get { return null; }
        }

        public bool Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            mm.RegisterInterface<IBandwidthLimit>(this);
            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IBandwidthLimit>();
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

    // TODO: dont understand what ASSS is doing with the calculations with millseconds, ticks, etc
    public class DefaultBandwithLimit : IModule, IBandwidthLimit
    {
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
            /// this array represents the percentage of traffic that is allowed to be at or lower than each priority level
            /// </summary>
            public static int[] PriorityLimits = new int[Enum.GetNames(typeof(BandwidthPriorities)).Length];

            /// <summary>
            /// we need to know how many packets the client is able to buffer
            /// </summary>
            public static int ClientCanBuffer;

            public static int LimitScale;
            public static int MaxAvail;
            public static bool UseHitLimit;

            #endregion

            private int _limit;
            private int[] _avail = new int[Enum.GetNames(typeof(BandwidthPriorities)).Length];
            private int _maxavail;
            private bool _hitlimit;
            private DateTime _sincetime;

            public BWLimit()
            {
                _limit = LimitLow; // start slow
                _maxavail = MaxAvail;
                _hitlimit = false;
                _sincetime = DateTime.Now;
            }

            #region IBWLimit Members

            public void Iter(DateTime now)
            {
                const int granularity = 8;
                int pri = 0;
                int slices = 0;

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

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            IConfigManager config = mm.GetInterface<IConfigManager>();
            if (config == null)
                return false;

            try
            {
                BWLimit.LimitLow = config.GetInt(config.Global, "Net", "LimitMinimum", 2500);
                BWLimit.LimitHigh = config.GetInt(config.Global, "Net", "LimitMaximum", 102400);
                BWLimit.ClientCanBuffer = config.GetInt(config.Global, "Net", "SendAtOnce", 30);
                BWLimit.LimitScale = config.GetInt(config.Global, "Net", "LimitScale", Constants.MaxPacket * 1);
                BWLimit.MaxAvail = config.GetInt(config.Global, "Net", "Burst", Constants.MaxPacket * 4);
                BWLimit.UseHitLimit = config.GetInt(config.Global, "Net", "UseHitLimit", 0) != 0;

                BWLimit.PriorityLimits[0] = config.GetInt(config.Global, "Net", "PriLimit0", 20); // low pri unrel
                BWLimit.PriorityLimits[1] = config.GetInt(config.Global, "Net", "PriLimit1", 40); // reg pri unrel
                BWLimit.PriorityLimits[2] = config.GetInt(config.Global, "Net", "PriLimit2", 20); // high pri unrel
                BWLimit.PriorityLimits[3] = config.GetInt(config.Global, "Net", "PriLimit3", 15); // rel
                BWLimit.PriorityLimits[4] = config.GetInt(config.Global, "Net", "PriLimit4", 5);  // ack
            }
            finally
            {
                mm.ReleaseInterface<IConfigManager>();
            }

            mm.RegisterInterface<IBandwidthLimit>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IBandwidthLimit>();
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
