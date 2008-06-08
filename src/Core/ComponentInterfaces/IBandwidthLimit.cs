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
}
