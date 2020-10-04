using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
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
        /// Gets the max range of reliable packets that can be buffered.
        /// <para>
        /// E.g., if X is the lowest pending (not yet sent OR sent but not yet acknolwedged) reliable sequence # for a connection, 
        /// then only allow sending of reliable packets with a sequence # less than or equal to:
        /// X + <see cref="GetCanBufferPackets"/>.
        /// </para>
        /// </summary>
        /// <returns>The range.</returns>
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
}
