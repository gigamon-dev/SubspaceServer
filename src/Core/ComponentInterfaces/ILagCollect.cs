using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public struct ClientLatencyData
    {
        public uint WeaponCount;
        public uint S2CSlowTotal;
        public uint S2CFastTotal;
        public ushort S2CSlowCurrent;
        public ushort S2CFastCurrent;
        public ushort Unknown1;
        public ushort LastPing;
        public ushort AveragePing;
        public ushort LowestPing;
        public ushort HighestPing;
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

        public void Update(in TimeSyncData data)
        {
            int sampleIndex = next;
            ServerTime[sampleIndex] = data.s_time;
            ClientTime[sampleIndex] = data.c_time;
            next = (sampleIndex + 1) % TimeSyncHistory.TimeSyncSamples;
        }
    }

    public interface ILagCollect : IComponentInterface
    {
        /// <summary>
        /// For collecting information when a client sends a position packet.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ms"></param>
        /// <param name="clipping"></param>
        /// <param name="wpnSent"></param>
        void Position(Player p, int ms, int clipping, uint wpnSent);

        /// <summary>
        /// For collecting information when a reliable acknowledgement packet arrives.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ms"></param>
        void RelDelay(Player p, int ms);

        /// <summary>
        /// For collecting information when a client responds to a security check.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        void ClientLatency(Player p, ref ClientLatencyData data);

        /// <summary>
        /// For collecting information when a time sync request arrives (0x00 0x05 core packet).
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        void TimeSync(Player p, in TimeSyncData data);

        /// <summary>
        /// For collecting information after processing the outgoing network queues.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        void RelStats(Player p, ref ReliableLagData data);

        /// <summary>
        /// Clears previously collected data for a player.
        /// </summary>
        /// <param name="p"></param>
        void Clear(Player p);
    }
}
