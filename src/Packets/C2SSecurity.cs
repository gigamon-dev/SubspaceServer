using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    /// <summary>
    /// Packet that clients respond with after receiving a <see cref="S2CSecurity"/> (<see cref="S2CPacketType.Security"/>) request.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SSecurity
    {
        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        /// <remarks>
        /// This is the minimum # of bytes.
        /// Continuum sends more data. Presumably for it to do additional checks with subgame?
        /// </remarks>
        public static readonly int Length;

        static C2SSecurity()
        {
            Length = Marshal.SizeOf<C2SSecurity>();
        }

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
            get { return LittleEndianConverter.Convert(weaponCount); }
            set { weaponCount = LittleEndianConverter.Convert(value); }
        }

        public uint SettingChecksum
        {
            get { return LittleEndianConverter.Convert(settingChecksum); }
            set { settingChecksum = LittleEndianConverter.Convert(value); }
        }

        public uint ExeChecksum
        {
            get { return LittleEndianConverter.Convert(exeChecksum); }
            set { exeChecksum = LittleEndianConverter.Convert(value); }
        }

        public uint MapChecksum
        {
            get { return LittleEndianConverter.Convert(mapChecksum); }
            set { mapChecksum = LittleEndianConverter.Convert(value); }
        }

        public uint S2CSlowTotal
        {
            get { return LittleEndianConverter.Convert(s2CSlowTotal); }
            set { s2CSlowTotal = LittleEndianConverter.Convert(value); }
        }

        public uint S2CFastTotal
        {
            get { return LittleEndianConverter.Convert(s2CFastTotal); }
            set { s2CFastTotal = LittleEndianConverter.Convert(value); }
        }

        public ushort S2CSlowCurrent

        {
            get { return LittleEndianConverter.Convert(s2CSlowCurrent); }
            set { s2CSlowCurrent = LittleEndianConverter.Convert(value); }
        }

        public ushort S2CFastCurrent
        {
            get { return LittleEndianConverter.Convert(s2CFastCurrent); }
            set { s2CFastCurrent = LittleEndianConverter.Convert(value); }
        }

        public ushort Unknown1
        {
            get { return LittleEndianConverter.Convert(unknown1); }
            set { unknown1 = LittleEndianConverter.Convert(value); }
        }

        public ushort LastPing
        {
            get { return LittleEndianConverter.Convert(lastPing); }
            set { lastPing = LittleEndianConverter.Convert(value); }
        }
        public ushort AveragePing
        {
            get { return LittleEndianConverter.Convert(averagePing); }
            set { averagePing = LittleEndianConverter.Convert(value); }
        }
        public ushort LowestPing
        {
            get { return LittleEndianConverter.Convert(lowestPing); }
            set { lowestPing = LittleEndianConverter.Convert(value); }
        }

        public ushort HighestPing
        {
            get { return LittleEndianConverter.Convert(highestPing); }
            set { highestPing = LittleEndianConverter.Convert(value); }
        }
    }
}
