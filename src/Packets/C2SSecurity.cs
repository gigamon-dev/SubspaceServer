using System;
using System.Buffers.Binary;
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
        private uint weaponCount;
        private uint settingChecksum;
        private uint exeChecksum;
        private uint mapChecksum;
        private uint s2CSlowTotal;
        private uint s2CFastTotal;
        private ushort s2CSlowCurrent;
        private ushort s2CFastCurrent;
        private ushort unknown1;
        private ushort lastPing;
        private ushort averagePing;
        private ushort lowestPing;
        private ushort highestPing;
        public byte SlowFrame;

        public uint WeaponCount
        {
            get { return BitConverter.IsLittleEndian? weaponCount : BinaryPrimitives.ReverseEndianness(weaponCount); }
            set { weaponCount = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public uint SettingChecksum
        {
            get { return BitConverter.IsLittleEndian ? settingChecksum : BinaryPrimitives.ReverseEndianness(settingChecksum); }
            set { settingChecksum = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public uint ExeChecksum
        {
            get { return BitConverter.IsLittleEndian ? exeChecksum : BinaryPrimitives.ReverseEndianness(exeChecksum); }
            set { exeChecksum = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public uint MapChecksum
        {
            get { return BitConverter.IsLittleEndian ? mapChecksum : BinaryPrimitives.ReverseEndianness(mapChecksum); }
            set { mapChecksum = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public uint S2CSlowTotal
        {
            get { return BitConverter.IsLittleEndian ? s2CSlowTotal : BinaryPrimitives.ReverseEndianness(s2CSlowTotal); }
            set { s2CSlowTotal = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public uint S2CFastTotal
        {
            get { return BitConverter.IsLittleEndian ? s2CFastTotal : BinaryPrimitives.ReverseEndianness(s2CFastTotal); }
            set { s2CFastTotal = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public ushort S2CSlowCurrent

        {
            get { return BitConverter.IsLittleEndian ? s2CSlowCurrent : BinaryPrimitives.ReverseEndianness(s2CSlowCurrent); }
            set { s2CSlowCurrent = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public ushort S2CFastCurrent

        {
            get { return BitConverter.IsLittleEndian ? s2CFastCurrent : BinaryPrimitives.ReverseEndianness(s2CFastCurrent); }
            set { s2CFastCurrent = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public ushort Unknown1
        {
            get { return BitConverter.IsLittleEndian ? unknown1 : BinaryPrimitives.ReverseEndianness(unknown1); }
            set { unknown1 = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public ushort LastPing
        {
            get { return BitConverter.IsLittleEndian ? lastPing : BinaryPrimitives.ReverseEndianness(lastPing); }
            set { lastPing = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }
        public ushort AveragePing
        {
            get { return BitConverter.IsLittleEndian ? averagePing : BinaryPrimitives.ReverseEndianness(averagePing); }
            set { averagePing = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }
        public ushort LowestPing
        {
            get { return BitConverter.IsLittleEndian ? lowestPing : BinaryPrimitives.ReverseEndianness(lowestPing); }
            set { lowestPing = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        public ushort HighestPing
        {
            get { return BitConverter.IsLittleEndian ? highestPing : BinaryPrimitives.ReverseEndianness(highestPing); }
            set { highestPing = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

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
