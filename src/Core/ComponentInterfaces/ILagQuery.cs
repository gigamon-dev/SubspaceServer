using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public struct PingSummary
    {
        public int curr, avg, min, max;

        // only used in QueryCPing
        public int s2cslowtotal;
        public int s2cfasttotal;
        public short s2cslowcurrent;
        public short s2cfastcurrent;
    }

    public struct PacketlossSummary
    {
        public double s2c, c2s, s2cwpn;
    }

    public struct ReliableLagData
    {
        /// <summary>
        /// the total number of duplicates that have been received
        /// </summary>
        public uint reldups;
            
        /// <summary>
        /// the reliable seqnum so far (i.e., the number of reliable packets that should have been received, excluding dups
        /// </summary>
        public uint c2sn;
        
        /// <summary>
        /// retries is the number of times the server has had to re-send a reliable packet.
        /// </summary>
        public uint retries;

        /// <summary>
        /// s2cn is the number of reliable packets that should have been sent, excluding retries.
        /// </summary>
        public uint s2cn;
    }

    public interface ILagQuery : IComponentInterface
    {
        /// <summary>
        /// To get ping info (from position packets)
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ping"></param>
        void QueryPPing(Player p, out PingSummary ping);

        /// <summary>
        /// To get ping info (reported by the client)
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ping"></param>
        void QueryCPing(Player p, out PingSummary ping);

        /// <summary>
        /// To get ping info (from reliable packets)
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ping"></param>
        void QueryRPing(Player p, out PingSummary ping);

        /// <summary>
        /// To get packetloss info
        /// </summary>
        /// <param name="p"></param>
        /// <param name="packetloss"></param>
        void QueryPLoss(Player p, out PacketlossSummary packetloss);

        /// <summary>
        /// To get reliable lag info
        /// </summary>
        /// <param name="p"></param>
        /// <param name="reliableLag"></param>
        void QueryRelLag(Player p, ReliableLagData reliableLag);

        // DoPHistogram
        // DoRHistogram
    }
}
