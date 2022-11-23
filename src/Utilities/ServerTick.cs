using System;
using System.Diagnostics;

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
        private static readonly long s_centisecondFrequency = Stopwatch.Frequency / 100;

        private readonly uint tickcount;

        public ServerTick(uint tickcount) => this.tickcount = tickcount & 0x7fffffff;

        /// <summary>
        /// Gets the current server time in ticks (1/100ths of a second).
        /// </summary>
        public static ServerTick Now => Stopwatch.IsHighResolution
            ? new((uint)(Stopwatch.GetTimestamp() / s_centisecondFrequency))
            : new((uint)Environment.TickCount / 10);

        public static bool operator >(ServerTick a, ServerTick b) => (a - b) > 0;

        public static bool operator <(ServerTick a, ServerTick b) => (a - b) < 0;

        public static bool operator >=(ServerTick a, ServerTick b) => (a - b) >= 0;

        public static bool operator <=(ServerTick a, ServerTick b) => (a - b) <= 0;

        public static int operator -(ServerTick a, ServerTick b) => (int)(((a.tickcount) << 1) - ((b.tickcount) << 1)) >> 1;

        public static ServerTick operator +(ServerTick a, uint b) => new(a.tickcount + b);

        public static ServerTick operator -(ServerTick a, uint b) => new(a.tickcount - b);

        public static bool operator ==(ServerTick a, ServerTick b) => a.Equals(b);

        public static bool operator !=(ServerTick a, ServerTick b) => !(a == b);

        public static ServerTick operator ++(ServerTick a) => a + 1;

        public static ServerTick operator --(ServerTick a) => a - (uint)1;

        public static implicit operator uint(ServerTick a) => a.tickcount;

        public static implicit operator ServerTick(uint tickcount) => new(tickcount);

        public override bool Equals(object obj) => obj is ServerTick tick && Equals(tick);

        public override int GetHashCode() => (int)tickcount;

        public override string ToString() => tickcount.ToString();

        #region IEquatable<ServerTick> Members

        public bool Equals(ServerTick other) => tickcount == other.tickcount;

        #endregion

        #region IComparable<ServerTick> Members

        public int CompareTo(ServerTick other) => this - other;

        #endregion
    }
}
