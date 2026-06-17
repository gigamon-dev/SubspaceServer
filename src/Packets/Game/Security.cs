using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that clients respond with after receiving a <see cref="S2C_Security"/> (<see cref="S2CPacketType.Security"/>) request.
    /// </summary>
    /// <remarks>
    /// Continuum sends the <see cref="C2S_SecurityContinuum"/> variation which includes additional fields.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_Security
    {
        #region Static Members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
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
        private ushort s2CAverageCurrent;
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

        public ushort S2CAverageCurrent
        {
            readonly get => LittleEndianConverter.Convert(s2CAverageCurrent);
            set => s2CAverageCurrent = LittleEndianConverter.Convert(value);
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
    /// Packet that Continuum clients respond with after receiving a <see cref="S2C_Security"/> (<see cref="S2CPacketType.Security"/>) request. 
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_SecurityContinuum
    {
        #region Static members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length = Marshal.SizeOf<C2S_SecurityContinuum>();

        #endregion

        public C2S_Security Basic;
        private short timerDrift;
        private uint mapCrc;

        #region Helper Properties

        public short TimerDrift
        {
            readonly get => LittleEndianConverter.Convert(timerDrift);
            set => timerDrift = LittleEndianConverter.Convert(value);
        }

        public uint MapCrc
        {
            readonly get => LittleEndianConverter.Convert(mapCrc);
            set => mapCrc = LittleEndianConverter.Convert(value);
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
            readonly get => LittleEndianConverter.Convert(greenSeed);
            set => greenSeed = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed
        {
            readonly get => LittleEndianConverter.Convert(doorSeed);
            set => doorSeed = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp
        {
            readonly get => LittleEndianConverter.Convert(timestamp);
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
            readonly get => LittleEndianConverter.Convert(key);
            set => key = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    /// <summary>
    /// Sent by the client when it detects a possible security violation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_SecurityViolationHeader
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<C2S_SecurityViolationHeader>();

        #endregion

        /// <summary>
        /// 0x1B - <see cref="C2SPacketType.SecurityViolation"/>
        /// </summary>
        public byte Type;
        private byte violation;
        // The header is optionally followed by a custom message (likely for violation type 0x3D). This might have been used for debugging purposes.

        #region Helper Properties

        public readonly ClientViolation Violation => (ClientViolation)violation;

        #endregion
    }

    /// <summary>
    /// Represents violations that the client may report.
    /// </summary>
    public enum ClientViolation : byte
    {
        None = 0,

        #region May only be sent in response to a security checksum request

        SlowFramerate = 0x01,

        /// <summary>
        /// Current energy is higher than top energy
        /// </summary>
        CurrentEnergyGreaterThanTop = 0x02,

        /// <summary>
        /// Top energy higher than max energy
        /// </summary>
        TopEnergyGreaterThanMax = 0x03,

        /// <summary>
        /// Max energy without getting prizes
        /// </summary>
        MaxEnergyWithoutPrizes = 0x04,

        /// <summary>
        /// Recharge rate higher than max recharge rate
        /// </summary>
        RechargeRateGreaterThanMax = 0x05,

        /// <summary>
        /// Max recharge rate without getting prizes 
        /// </summary>
        MaxRechargeRateWithoutPrizes = 0x06,

        /// <summary>
        /// Too many burst used (More than you have)
        /// </summary>
        BurstUseExceeded = 0x07,

        /// <summary>
        /// Too many repel used
        /// </summary>
        RepelUseExceeded = 0x08,

        /// <summary>
        /// Too many decoy used (More than you have)
        /// </summary>
        DecoyUseExceeded = 0x09,

        /// <summary>
        /// Too many thor used (More than you have)
        /// </summary>
        ThorUseExceeded = 0x0A,

        /// <summary>
        /// Too many wall blocks used (More than you have)
        /// </summary>
        BrickUseExceeded = 0x0B,

        /// <summary>
        /// Stealth on but never greened it
        /// </summary>
        StealthOnWithoutObtaining = 0x0C,

        /// <summary>
        /// Cloak on but never greened it
        /// </summary>
        CloakOnWithoutObtaining = 0x0D,

        /// <summary>
        /// XRadar on but never greened it
        /// </summary>
        XRadarOnWithoutObtaining = 0x0E,

        /// <summary>
        /// AntiWarp on but never greened it
        /// </summary>
        AntiWarpOnWithoutObtaining = 0x0F,

        /// <summary>
        /// Proximity bombs but never greened it
        /// </summary>
        ProxBombsWithoutObtaining = 0x10,

        /// <summary>
        /// Bouncing bullets but never greened it
        /// </summary>
        BouncingBulletsWithoutObtaining = 0x11,

        /// <summary>
        /// Max guns without greening
        /// </summary>
        MaxGunsWithoutObtaining = 0x12,

        /// <summary>
        /// Max bombs without greening
        /// </summary>
        MaxBombsWithoutObtaining = 0x13,

        /// <summary>
        /// Shields or Super on longer than possible
        /// </summary>
        ShieldsOrSuperDurationExceeded = 0x14,

        #endregion

        #region Can be sent at any time

        /// <summary>
        /// Saved ship weapon limits too high (burst/repel/etc)
        /// </summary>
        ShipWeaponLimitExceeded = 0x15,

        /// <summary>
        /// Saved ship weapon level too high (guns/bombs)
        /// </summary>
        ShipWeaponLevelExceeded = 0x16,

        /// <summary>
        /// Login checksum mismatch (program exited)
        /// </summary>
        LoginChecksumMismatch = 0x17,

        Unknown = 0x18,

        /// <summary>
        /// Saved ship checksum mismatch
        /// </summary>
        ShipChecksumMismatch = 0x19,

        #endregion

        #region May only be sent in response to a security checksum request

        /// <summary>
        /// Softice Debugger Running
        /// </summary>
        SoftIceDebuggerDetected = 0x1A,

        /// <summary>
        /// Data checksum mismatch
        /// </summary>
        DataChecksumMismatch = 0x1B,

        /// <summary>
        /// Parameter mismatch
        /// </summary>
        ParameterMismatch = 0x1C,

        /// <summary>
        /// High latency in Continuum
        /// </summary>
        HighLatency = 0x3C,

        /// <summary>
        /// Custom violation.
        /// </summary>
        /// <remarks>
        /// The 0x1B (Security Violation) packet may be followed by additional bytes containing a custom message.
        /// </remarks>
        Custom = 0x3D,

        /// <summary>
        /// Memory Altered Checksum error
        /// </summary>
        MemoryAltered = 0x3E,

        #endregion
    }
}
