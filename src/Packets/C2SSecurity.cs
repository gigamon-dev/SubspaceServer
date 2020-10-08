using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Packets
{
    /// <summary>
    /// Packet that clients respond with after receiving a <see cref="S2CSecurity"/> (<see cref="S2CPacketType.Security"/>) request.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SSecurity
    {
        /// <summary>
        /// 0x1A
        /// </summary>
        public byte Type;
        public uint WeaponCount;
        public uint SettingChecksum;
        public uint ExeChecksum;
        public uint MapChecksum;
        public uint S2CSlowTotal;
        public uint S2CFastTotal;
        public ushort S2CSlowCurrent;
        public ushort S2CFastCurrent;
        public ushort Unknown1;
        public ushort LastPing;
        public ushort AveragePing;
        public ushort LowestPing;
        public ushort HighestPing;
        public byte SlowFrame;

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        /// <remarks>
        /// This is the minimum # of bytes.
        /// Continuum sends more data. Presumably for it to do additional checks with subgame?
        /// </remarks>
        public const int Length = 40;
    }
}
