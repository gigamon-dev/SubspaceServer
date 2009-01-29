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

    public class TimeSyncHistory
    {
        private const int TimeSyncSamples = 10;

        public uint[] ServerTime = new uint[TimeSyncSamples];
        public uint[] ClientTime = new uint[TimeSyncSamples];
        private int next;
        public int drift;

        public void Update(ref TimeSyncData data)
        {
            int sampleIndex = next;
            ServerTime[sampleIndex] = data.s_time;
            ClientTime[sampleIndex] = data.c_time;
            next = (sampleIndex + 1) % TimeSyncHistory.TimeSyncSamples;
        }
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
