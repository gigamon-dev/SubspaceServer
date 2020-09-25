using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// Represents a time, either absolute or relative.
    /// Ticks are 31 bits in size. The value is stored in the lower 31 bits
    /// of an unsigned int. 
    /// 
    /// Don't do arithmetic on these directly, use this struct's methods.
    /// 
    /// TickCount with a graularity in 1/100ths of a second.
    /// Note: Parts of the SS protocol report in 1/100ths of a second.
    /// - time sync packets
    /// - player position packets
    /// 
    /// 100% Equivalent to ASSS' ticks_t
    /// </summary>
    public readonly struct ServerTick : IEquatable<ServerTick>, IComparable<ServerTick>
    {
        private readonly uint tickcount;

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

        public static ServerTick operator +(ServerTick a, uint b)
        {
            return new ServerTick(makeTick(a.tickcount + b));
        }

        public static bool operator ==(ServerTick a, ServerTick b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(ServerTick a, ServerTick b)
        {
            return !(a == b);
        }

        public static implicit operator uint(ServerTick a)
        {
            return a.tickcount;
        }

        private static uint makeTick(int a)
        {
            return (uint)((a) & 0x7fffffff);
        }

        private static uint makeTick(uint a)
        {
            return a & 0x7fffffff;
        }

        public override bool Equals(object obj)
        {
            if (obj is ServerTick)
                return Equals((ServerTick)obj);
            else
                return false;
        }

        public override int GetHashCode()
        {
            return (int)tickcount;
        }

        public override string ToString()
        {
            return tickcount.ToString();
        }

        #region IEquatable<ServerTick> Members

        public bool Equals(ServerTick other)
        {
            return tickcount == other.tickcount;
        }

        #endregion

        #region IComparable<ServerTick> Members

        public int CompareTo(ServerTick other)
        {
            return this - other;
        }

        #endregion
    }
}
