using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// represents a time, either absolute or relative.
    /// ticks are 31 bits in size. the value is stored in the lower 31 bits
    /// of an unsigned int. 
    /// 
    /// don't do arithmatic on these directly, use this class' methods
    /// 
    /// TickCount with a graularity in 1/100ths of a second.
    /// Note: Parts of the SS protocol report in 1/100ths of a second.
    /// - time sync packets
    /// - player position packets
    /// 
    /// 100% Equivalent to ASSS' ticks_t
    /// </summary>
    public struct ServerTick
    {
        private uint tickcount;

        public ServerTick(uint tickcount)
        {
            this.tickcount = tickcount;
        }

        /// <summary>
        /// gets the current server time in ticks (1/100ths of a second)
        /// </summary>
        public static ServerTick Now
        {
            get { return new ServerTick(makeTick(Environment.TickCount / 10)); }
        }

        public static bool operator >(ServerTick a, ServerTick b)
        {
            return (a - b) > 0;
        }

        public static bool operator <(ServerTick a, ServerTick b)
        {
            return (a - b) < 0;
        }

        public static int operator -(ServerTick a, ServerTick b)
        {
            return ((int)(((a.tickcount) << 1) - ((b.tickcount) << 1)) >> 1);
        }

        public static implicit operator uint(ServerTick a)
        {
            return a.tickcount;
        }

        private static uint makeTick(int a)
        {
            return (uint)((a) & 0x7fffffff);
        }

        public override string ToString()
        {
            return tickcount.ToString();
        }
    }
}
