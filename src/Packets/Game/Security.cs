using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that clients respond with after receiving a <see cref="S2C_Security"/> (<see cref="S2CPacketType.Security"/>) request.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_Security
    {
        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        /// <remarks>
        /// This is the minimum # of bytes.
        /// Continuum sends more data. Presumably for it to do additional checks with subgame?
        /// </remarks>
        public static readonly int Length;

        static C2S_Security()
        {
            Length = Marshal.SizeOf<C2S_Security>();
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

    /// <summary>
    /// Packet that the server sends to either:
    /// <list type="bullet">
    /// <item>synchronize a client when the player enters an arena</item>
    /// <item>request a client respond with a <see cref="C2S_Security"/> (<see cref="C2SPacketType.SecurityResponse"/>)</item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Security
    {
        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        static S2C_Security()
        {
            Length = Marshal.SizeOf<S2C_Security>();
        }

        /// <summary>
        /// 0x18
        /// </summary>
        public byte Type;
        private uint greenSeed;
        private uint doorSeed;
        private uint timestamp;
        private uint key;

        /// <summary>
        /// Seed for greens.
        /// </summary>
        public uint GreenSeed
        {
            get { return LittleEndianConverter.Convert(greenSeed); }
            set { greenSeed = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed
        {
            get { return LittleEndianConverter.Convert(doorSeed); }
            set { doorSeed = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp
        {
            get { return LittleEndianConverter.Convert(timestamp); }
            set { timestamp = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// Key for checksum use.
        /// <para>
        /// 0 when just syncing a client up (when a player enters an arena).
        /// </para>
        /// <para>
        /// Non-zero for requesting that the client respond to a security check.
        /// </para>
        /// </summary>
        public uint Key
        {
            get { return LittleEndianConverter.Convert(key); }
            set { key = LittleEndianConverter.Convert(value); }
        }
    }
}
