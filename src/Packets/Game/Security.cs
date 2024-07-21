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
        #region Static Members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        /// <remarks>
        /// This is the minimum # of bytes.
        /// Continuum sends more data. Presumably for it to do additional checks with subgame?
        /// </remarks>
        public static readonly int Length = Marshal.SizeOf<C2S_Security>();

        #endregion

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

        #region Helper Properties

        public uint WeaponCount
        {
            readonly get => LittleEndianConverter.Convert(weaponCount);
            set => weaponCount = LittleEndianConverter.Convert(value);
        }

        public uint SettingChecksum
        {
            readonly get => LittleEndianConverter.Convert(settingChecksum);
            set => settingChecksum = LittleEndianConverter.Convert(value);
        }

        public uint ExeChecksum
        {
            readonly get => LittleEndianConverter.Convert(exeChecksum);
            set => exeChecksum = LittleEndianConverter.Convert(value);
        }

        public uint MapChecksum
        {
            readonly get => LittleEndianConverter.Convert(mapChecksum);
            set => mapChecksum = LittleEndianConverter.Convert(value);
        }

        public uint S2CSlowTotal
        {
            readonly get => LittleEndianConverter.Convert(s2CSlowTotal);
            set => s2CSlowTotal = LittleEndianConverter.Convert(value);
        }

        public uint S2CFastTotal
        {
            readonly get => LittleEndianConverter.Convert(s2CFastTotal);
            set => s2CFastTotal = LittleEndianConverter.Convert(value);
        }

        public ushort S2CSlowCurrent

        {
            readonly get => LittleEndianConverter.Convert(s2CSlowCurrent);
            set => s2CSlowCurrent = LittleEndianConverter.Convert(value);
        }

        public ushort S2CFastCurrent
        {
            readonly get => LittleEndianConverter.Convert(s2CFastCurrent);
            set => s2CFastCurrent = LittleEndianConverter.Convert(value);
        }

        public ushort Unknown1
        {
            readonly get => LittleEndianConverter.Convert(unknown1);
            set => unknown1 = LittleEndianConverter.Convert(value);
        }

        public ushort LastPing
        {
            readonly get => LittleEndianConverter.Convert(lastPing);
            set => lastPing = LittleEndianConverter.Convert(value);
        }
        public ushort AveragePing
        {
            readonly get => LittleEndianConverter.Convert(averagePing);
            set => averagePing = LittleEndianConverter.Convert(value);
        }
        public ushort LowestPing
        {
            readonly get => LittleEndianConverter.Convert(lowestPing);
            set => lowestPing = LittleEndianConverter.Convert(value);
        }

        public ushort HighestPing
        {
            readonly get => LittleEndianConverter.Convert(highestPing);
            set => highestPing = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    /// <summary>
    /// Packet that the server sends to either:
    /// <list type="bullet">
    /// <item>synchronize a client when the player enters an arena</item>
    /// <item>request a client respond with a <see cref="C2S_Security"/> (<see cref="C2SPacketType.SecurityResponse"/>)</item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Security(uint greenSeed, uint doorSeed, uint timestamp, uint key)
    {
        #region Static Members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length = Marshal.SizeOf<S2C_Security>();

        #endregion

        /// <summary>
        /// 0x18
        /// </summary>
        public byte Type = (byte)S2CPacketType.Security;
        private uint greenSeed = LittleEndianConverter.Convert(greenSeed);
        private uint doorSeed = LittleEndianConverter.Convert(doorSeed);
        private uint timestamp = LittleEndianConverter.Convert(timestamp);
        private uint key = LittleEndianConverter.Convert(key);

        public S2C_Security() : this(0, 0, 0, 0)
        {
        }

        #region Helper Properties

        /// <summary>
        /// Seed for greens.
        /// </summary>
        public uint GreenSeed
        {
            get => LittleEndianConverter.Convert(greenSeed);
            set => greenSeed = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed
        {
            get => LittleEndianConverter.Convert(doorSeed);
            set => doorSeed = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp
        {
            get => LittleEndianConverter.Convert(timestamp);
            set => timestamp = LittleEndianConverter.Convert(value);
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
            get => LittleEndianConverter.Convert(key);
            set => key = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
