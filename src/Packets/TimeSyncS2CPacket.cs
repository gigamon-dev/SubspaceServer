﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public readonly struct TimeSyncS2CPacket
    {
        // static constructor to initialize packet's info
        static TimeSyncS2CPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateByteDataLocation();
            t2 = locationBuilder.CreateByteDataLocation();
            clienttime = locationBuilder.CreateUInt32DataLocation();
            servertime = locationBuilder.CreateUInt32DataLocation();
            Length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation t1;
        private static readonly ByteDataLocation t2;
        private static readonly UInt32DataLocation clienttime;
        private static readonly UInt32DataLocation servertime;
        public static int Length { get; }

        // data members
        private readonly byte[] data;

        public TimeSyncS2CPacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public void Initialize()
        {
            T1 = 0x00;
            T2 = 0x06;
        }

        public void Initialize(uint clientType, uint serverTime)
        {
            Initialize();

            ClientTime = clientType;
            ServerTime = serverTime;
        }

        public byte T1
        {
            set { t1.SetValue(data, value); }
        }

        public byte T2
        {
            set { t2.SetValue(data, value); }
        }

        public uint ClientTime
        {
            set { clienttime.SetValue(data, value); }
        }

        public uint ServerTime
        {
            set { servertime.SetValue(data, value); }
        }
    }
}
