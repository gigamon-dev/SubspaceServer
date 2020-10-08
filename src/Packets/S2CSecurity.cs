using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2CSecurity
    {
        /// <summary>
        /// 0x18
        /// </summary>
        public byte Type;

        /// <summary>
        /// Seed for greens.
        /// </summary>
        public uint GreenSeed;

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed;

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp;

        /// <summary>
        /// Key for checksum use.
        /// </summary>
        public uint Key;
    }
}
