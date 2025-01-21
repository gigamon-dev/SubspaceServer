using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [Flags]
    public enum PlayerPositionStatus : byte
    {
        /// <summary>
        /// whether stealth is on
        /// </summary>
        Stealth = 1,

        /// <summary>
        /// whether cloak is on
        /// </summary>
        Cloak = 2,

        /// <summary>
        /// whether xradar is on
        /// </summary>
        XRadar = 4,

        /// <summary>
        /// whether antiwarp is on
        /// </summary>
        Antiwarp = 8,

        /// <summary>
        /// whether to display the flashing image for a few frames
        /// </summary>
        Flash = 16,

        /// <summary>
        /// whether the player is in a safezone
        /// </summary>
        Safezone = 32,

        /// <summary>
        /// whether the player is a ufo
        /// </summary>
        Ufo = 64
    }

    public enum WeaponCodes : byte
    {
        Null = 0,
        Bullet = 1,
        BounceBullet = 2,
        Bomb = 3,
        ProxBomb = 4,
        Repel = 5,
        Decoy = 6,
        Burst = 7,
        Thor = 8,

        /// <summary>
        /// used in watch damage packet only
        /// </summary>
        Wormhole = 0,

        /// <summary>
        /// used in watch damage packet only
        /// </summary>
        Shrapnel = 15,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WeaponData
    {
        #region Static Members

        public const int Length = 2;

        #endregion

        private byte bitfield1;
        private byte bitfield2;

        #region Helper Properties

        // bitfield1 masks
        private const byte TypeMask = 0b00011111;
        private const byte LevelMask = 0b01100000;
        private const byte SharpBouncingMask = 0b10000000;

        // bitfield2 masks
        private const byte ShrapLevelMask = 0b00000011;
        private const byte ShrapMask = 0b01111100;
        private const byte AlternateMask = 0b10000000;

        public WeaponCodes Type
        {
            readonly get => (WeaponCodes)(bitfield1 & TypeMask);
            set => bitfield1 = (byte)((bitfield1 & ~TypeMask) | ((byte)value & TypeMask));
        }

        public byte Level
        {
            readonly get => (byte)((bitfield1 & LevelMask) >> 5);
            set => bitfield1 = (byte)((bitfield1 & ~LevelMask) | (value << 5 & LevelMask));
        }

        public bool ShrapBouncing
        {
            readonly get => ((bitfield1 & SharpBouncingMask) >> 7) != 0;
            set => bitfield1 = (byte)((bitfield1 & ~SharpBouncingMask) | ((value ? 1 : 0) << 7 & SharpBouncingMask));
        }

        public byte ShrapLevel
        {
            readonly get => (byte)(bitfield2 & ShrapLevelMask);
            set => bitfield2 = (byte)((bitfield2 & ~ShrapLevelMask) | (value & ShrapLevelMask));
        }

        public byte Shrap
        {
            readonly get => (byte)((bitfield2 & ShrapMask) >> 2);
            set => bitfield2 = (byte)((bitfield2 & ~ShrapMask) | (value << 2 & ShrapMask));
        }

        public bool Alternate
        {
            readonly get => (byte)((bitfield2 & AlternateMask) >> 7) != 0;
            set => bitfield2 = (byte)((bitfield2 & ~AlternateMask) | ((value ? 1 : 0) << 7 & AlternateMask));
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ExtraPositionData
    {
        #region Static Members

        public const int Length = 10;

        #endregion

        private ushort energy;
        private ushort s2cping;
        private ushort timer;
        private uint bitfield;

        #region Helper Properties

        // bitfield masks
        private const uint ShieldsMask = 0b00000000_00000000_00000000_00000001;
        private const uint SuperMask = 0b00000000_00000000_00000000_00000010;
        private const uint BurstsMask = 0b00000000_00000000_00000000_00111100;
        private const uint RepelsMask = 0b00000000_00000000_00000011_11000000;
        private const uint ThorsMask = 0b00000000_00000000_00111100_00000000;
        private const uint BricksMask = 0b00000000_00000011_11000000_00000000;
        private const uint DecoysMask = 0b00000000_00111100_00000000_00000000;
        private const uint RocketsMask = 0b00000011_11000000_00000000_00000000;
        private const uint PortalsMask = 0b00111100_00000000_00000000_00000000;
        private const uint PaddingMask = 0b11000000_00000000_00000000_00000000; // unused bits?

        public ushort Energy
        {
            readonly get => LittleEndianConverter.Convert(energy);
            set => energy = LittleEndianConverter.Convert(value);
        }

        public ushort S2CPing
        {
            readonly get => LittleEndianConverter.Convert(s2cping);
            set => s2cping = LittleEndianConverter.Convert(value);
        }

        public ushort Timer
        {
            readonly get => LittleEndianConverter.Convert(timer);
            set => timer = LittleEndianConverter.Convert(value);
        }

        private uint BitField
        {
            readonly get => LittleEndianConverter.Convert(bitfield);
            set => bitfield = LittleEndianConverter.Convert(value);
        }

        public bool Shields
        {
            readonly get => (byte)(BitField & ShieldsMask) != 0;
            set => BitField = (BitField & ~ShieldsMask) | ((value ? 1u : 0u) & ShieldsMask);
        }

        public bool Super
        {
            readonly get => (byte)(BitField & SuperMask) >> 1 != 0;
            set => BitField = (BitField & ~SuperMask) | ((value ? 1u : 0u) << 1 & SuperMask);
        }

        public byte Bursts
        {
            readonly get => (byte)((BitField & BurstsMask) >> 2);
            set => BitField = (BitField & ~BurstsMask) | ((uint)value << 2 & BurstsMask);
        }

        public byte Repels
        {
            readonly get => (byte)((BitField & RepelsMask) >> 6);
            set => BitField = (BitField & ~RepelsMask) | ((uint)value << 6 & RepelsMask);
        }

        public byte Thors
        {
            readonly get => (byte)((BitField & ThorsMask) >> 10);
            set => BitField = (BitField & ~ThorsMask) | ((uint)value << 10 & ThorsMask);
        }

        public byte Bricks
        {
            readonly get => (byte)((BitField & BricksMask) >> 14);
            set => BitField = (BitField & ~BricksMask) | ((uint)value << 14 & BricksMask);
        }

        public byte Decoys
        {
            readonly get => (byte)((BitField & DecoysMask) >> 18);
            set => BitField = (BitField & ~DecoysMask) | ((uint)value << 18 & DecoysMask);
        }

        public byte Rockets
        {
            readonly get => (byte)((BitField & RocketsMask) >> 22);
            set => BitField = (BitField & ~RocketsMask) | ((uint)value << 22 & RocketsMask);
        }

        public byte Portals
        {
            readonly get => (byte)((BitField & PortalsMask) >> 26);
            set => BitField = (BitField & ~PortalsMask) | ((uint)value << 26 & PortalsMask);
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_WeaponsPacket()
    {
        #region Static Members

        /// <summary>
        /// Length without extra position data.
        /// </summary>
        public const int Length = 19 + WeaponData.Length;

        /// <summary>
        /// Length with extra position data.
        /// </summary>
        public const int LengthWithExtra = Length + ExtraPositionData.Length;

        #endregion

        public byte Type = 0x05;

        /// <summary>
        /// Ship rotation [0-39]. Where 0 is 12:00, 10 is 3:00, 20 is 6:00, and 30 is 9:00.
        /// </summary>
        public sbyte Rotation;
        private ushort time;
        private short x;
        private short ySpeed;
        private ushort playerId;
        private short xSpeed;
        public byte Checksum;
        private byte status;
        public byte C2SLatency;
        private short y;
        private ushort bounty;
        public WeaponData Weapon;
        public ExtraPositionData Extra;

        #region Helper Properties

        public ushort Time
        {
            readonly get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

        public short X
        {
            readonly get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public short YSpeed
        {
            readonly get => LittleEndianConverter.Convert(ySpeed);
            set => ySpeed = LittleEndianConverter.Convert(value);
        }

        public ushort PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public short XSpeed
        {
            readonly get => LittleEndianConverter.Convert(xSpeed);
            set => xSpeed = LittleEndianConverter.Convert(value);
        }

        public PlayerPositionStatus Status
        {
            readonly get => (PlayerPositionStatus)LittleEndianConverter.Convert(status);
            set => status = (byte)value;
        }

        public short Y
        {
            readonly get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public ushort Bounty
        {
            readonly get => LittleEndianConverter.Convert(bounty);
            set => bounty = LittleEndianConverter.Convert(value);
        }

        #endregion

        public void SetChecksum()
        {
            Checksum = 0;

            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
            byte ck = 0;
            for (int i = 0; i < Length; i++)
                ck ^= data[i];

            Checksum = ck;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_PositionPacket()
    {
        #region Static Members

        public const int Length = 16;
        public const int LengthWithExtra = Length + ExtraPositionData.Length;

        #endregion

        public byte Type = 0x28;

        /// <summary>
        /// Ship rotation [0-39]. Where 0 is 12:00, 10 is 3:00, 20 is 6:00, and 30 is 9:00.
        /// </summary>
        public sbyte Rotation;
        private ushort time;
        private short x;
        public byte C2SLatency;
        public byte Bounty;
        public byte PlayerId;
        private byte status;
        private short ySpeed;
        private short y;
        private short xSpeed;
        public ExtraPositionData Extra;

        #region Helper Properties

        public ushort Time
        {
            readonly get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

        public short X
        {
            readonly get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public PlayerPositionStatus Status
        {
            readonly get => (PlayerPositionStatus)LittleEndianConverter.Convert(status);
            set => status = (byte)value;
        }

        public short YSpeed
        {
            readonly get => LittleEndianConverter.Convert(ySpeed);
            set => ySpeed = LittleEndianConverter.Convert(value);
        }

        public short Y
        {
            readonly get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public short XSpeed
        {
            readonly get => LittleEndianConverter.Convert(xSpeed);
            set => xSpeed = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_PositionPacket()
    {
        #region Static Members

        /// <summary>
        /// Length without extra position data.
        /// </summary>
        public const int Length = 20 + WeaponData.Length;

        /// <summary>
        /// Length with extra position data.
        /// </summary>
        public const int LengthWithExtra = Length + ExtraPositionData.Length;

        #endregion

        public byte Type = 0x03;

        /// <summary>
        /// Ship rotation [0-39]. Where 0 is 12:00, 10 is 3:00, 20 is 6:00, and 30 is 9:00.
        /// </summary>
        public sbyte Rotation;
        private uint time;
        private short xSpeed;
        private short y;
        public byte Checksum;
        private byte status;
        private short x;
        private short ySpeed;
        private ushort bounty;
        private short energy;
        public WeaponData Weapon;
        // Optionally followed by ExtraPositionData

        #region Helper Properties

        public ServerTick Time
        {
            readonly get => new(LittleEndianConverter.Convert(time));
            set => time = LittleEndianConverter.Convert(value);
        }

        public short XSpeed
        {
            readonly get => LittleEndianConverter.Convert(xSpeed);
            set => xSpeed = LittleEndianConverter.Convert(value);
        }

        public short Y
        {
            readonly get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public PlayerPositionStatus Status
        {
            readonly get => (PlayerPositionStatus)status;
            set => status = (byte)value;
        }

        public short X
        {
            readonly get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public short YSpeed
        {
            readonly get => LittleEndianConverter.Convert(ySpeed);
            set => ySpeed = LittleEndianConverter.Convert(value);
        }

        public ushort Bounty
        {
            readonly get => LittleEndianConverter.Convert(bounty);
            set => bounty = LittleEndianConverter.Convert(value);
        }

        public short Energy
        {
            readonly get => LittleEndianConverter.Convert(energy);
            set => energy = LittleEndianConverter.Convert(value);
        }

        #endregion

        public void SetChecksum()
        {
            Checksum = 0;

            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
            byte ck = 0;
            for (int i = 0; i < Length; i++)
                ck ^= data[i];

            Checksum = ck;
        }

        public bool IsValidChecksum
        {
            get
            {
                ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
                byte checksum = 0;
                int left = Length;
                while ((left--) > 0)
                    checksum ^= data[left];

                return checksum == 0;
            }
        }
    }
}
