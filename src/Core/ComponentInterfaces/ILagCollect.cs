using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public struct ClientLatencyData
    {
        public uint weaponcount;
        public uint s2cslowtotal;
        public uint s2cfasttotal;
        public ushort s2cslowcurrent;
        public ushort s2cfastcurrent;
        public ushort unknown1;
        public short lastping;
        public short averageping;
        public short lowestping;
        public short highestping;
    }

    public struct TimeSyncData
    {
        /* what the server thinks */
        public uint s_pktrcvd, s_pktsent;

	    /* what the client reports */
        public uint c_pktrcvd, c_pktsent;

	    /* time sync */
        public uint s_time, c_time;
    }

    public interface ILagCollect : IComponentInterface
    {
        void Position(Player p, int ms, int clipping, uint wpnSent);
        void RelDelay(Player p, int ms);
        void ClientLatency(Player p, ref ClientLatencyData data);
        void TimeSync(Player p, ref TimeSyncData data);
        void RelStats(Player p, ref ReliableLagData data);
        void Clear(Player p);
    }
}
