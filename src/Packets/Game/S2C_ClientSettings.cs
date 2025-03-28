﻿using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_ClientSettings
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<S2C_ClientSettings>();

        #endregion

        public ClientBits BitSet;
        public AllShipSettings Ships;
        public Int32InlineArray Int32Settings;
        public SpawnPositions SpawnPositions;
        public Int16InlineArray Int16Settings;
        public ByteInlineArray ByteSettings;
        public PrizeWeightInlineArray PrizeWeightSettings;

        #region Helper members

        public byte Type
        {
            get => BitSet.Type;
            set => BitSet.Type = value;
        }

        public uint GetChecksum(uint key)
        {
            ReadOnlySpan<byte> byteSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
            uint checksum = 0;

            while (byteSpan.Length >= 4)
            {
                checksum += (BinaryPrimitives.ReadUInt32LittleEndian(byteSpan) ^ key);
                byteSpan = byteSpan[4..];
            }

            return checksum;
        }

        #endregion

        #region Inline Array Types

        [InlineArray(Length)]
        public struct Int32InlineArray
        {
            public const int Length = 20;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private int _element0;
        }

        [InlineArray(Length)]
        public struct Int16InlineArray
        {
            public const int Length = 58;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private short _element0;
        }

        [InlineArray(Length)]
        public struct ByteInlineArray
        {
            public const int Length = 32;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;
        }

        [InlineArray(Length)]
        public struct PrizeWeightInlineArray
        {
            public const int Length = 28;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClientBits
    {
        private uint bitfield;

        // masks
        private const uint TypeMask                    = 0b_00000000_00000000_00000000_11111111;
        private const uint ExactDamageMask             = 0b_00000000_00000000_00000001_00000000;
        private const uint HideFlagsMask               = 0b_00000000_00000000_00000010_00000000;
        private const uint NoXRadarMask                = 0b_00000000_00000000_00000100_00000000;
        private const uint SlowFramerateMask           = 0b_00000000_00000000_00111000_00000000;
        private const uint DisableScreenshotMask       = 0b_00000000_00000000_01000000_00000000;
        private const uint MaxTimerDriftMask           = 0b_00000000_00000111_00000000_00000000;
        private const uint DisableBallThroughWallsMask = 0b_00000000_00001000_00000000_00000000;
        private const uint DisableBallKillingMask      = 0b_00000000_00010000_00000000_00000000;

        private uint BitField
        {
            get => LittleEndianConverter.Convert(bitfield);
            set => bitfield = LittleEndianConverter.Convert(value);
        }

        public byte Type
        {
            get => (byte)(BitField & TypeMask);
            set => BitField = (BitField & ~TypeMask) | (value & TypeMask);
        }

        public bool ExactDamage
        {
            get => (BitField & ExactDamageMask) != 0;
            set => BitField = (BitField & ~ExactDamageMask) | (value ? ExactDamageMask : 0);
        }

        public bool HideFlags
        {
            get => (BitField & HideFlagsMask) != 0;
            set => BitField = (BitField & ~HideFlagsMask) | (value ? HideFlagsMask : 0);
        }

        public bool NoXRadar
        {
            get => (BitField & NoXRadarMask) != 0;
            set => BitField = (BitField & ~NoXRadarMask) | (value ? NoXRadarMask : 0);
        }

        public byte SlowFramerate
        {
            get => (byte)((BitField & SlowFramerateMask) >> 11);
            set => BitField = (BitField & ~SlowFramerateMask) | (((uint)value << 11) & SlowFramerateMask);
        }

        public bool DisableScreenshot
        {
            get => (BitField & DisableScreenshotMask) != 0;
            set => BitField = (BitField & ~DisableScreenshotMask) | (value ? DisableScreenshotMask : 0);
        }

        public byte MaxTimerDrift
        {
            get => (byte)((BitField & MaxTimerDriftMask) >> 16);
            set => BitField = (BitField & ~MaxTimerDriftMask) | (((uint)value << 16) & MaxTimerDriftMask);
        }

        public bool DisableWallPass
        {
            get => (BitField & DisableBallThroughWallsMask) != 0;
            set => BitField = (BitField & ~DisableBallThroughWallsMask) | (value ? DisableBallThroughWallsMask : 0);
        }

        public bool DisableBallKilling
        {
            get => (BitField & DisableBallKillingMask) != 0;
            set => BitField = (BitField & ~DisableBallKillingMask) | (value ? DisableBallKillingMask : 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AllShipSettings
    {
        public ShipSettings Warbird;
        public ShipSettings Javelin;
        public ShipSettings Spider;
        public ShipSettings Leviathan;
        public ShipSettings Terrier;
        public ShipSettings Weasel;
        public ShipSettings Lancaster;
        public ShipSettings Shark;

        private Span<ShipSettings> Ships => MemoryMarshal.CreateSpan(ref Warbird, 8);

        public ref ShipSettings this[int index]
        {
            get
            {
                if (index < 0 || index > 7)
                    throw new IndexOutOfRangeException();

                return ref Ships[index];
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShipSettings
    {
        public Int32InlineArray Int32Settings;
        public Int16InlineArray Int16Settings; // [10] is the MiscBits bitfield
        public ByteInlineArray ByteSettings;
        public WeaponBits Weapons;
        private PaddingInlineArray padding;

        #region Inline Array Types

        [InlineArray(Length)]
        public struct Int32InlineArray
        {
            public const int Length = 2;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private int _element0;
        }

        [InlineArray(Length)]
        public struct Int16InlineArray
        {
            public const int Length = 49;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private short _element0;
        }

        [InlineArray(Length)]
        public struct ByteInlineArray
        {
            public const int Length = 18;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;
        }

        [InlineArray(Length)]
        public struct PaddingInlineArray
        {
            public const int Length = 16;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MiscBits
    {
        private ushort bitfield;

        private const ushort SeeBombLevelMask        = 0b_00000000_00000011;
        private const ushort DisableFastShootingMask = 0b_00000000_00000100;
        private const ushort RadiusMask              = 0b_00000111_11111000;

        private ushort BitField
        {
            get => LittleEndianConverter.Convert(bitfield);
            set => bitfield = LittleEndianConverter.Convert(value);
        }

        public byte SeeBombLevel
        {
            get => (byte)(BitField & SeeBombLevelMask);
            set => BitField = (ushort)((BitField & ~SeeBombLevelMask) | (value & SeeBombLevelMask));
        }

        public bool DisableFastShooting
        {
            get => (BitField & DisableFastShootingMask) != 0;
            set => BitField = (ushort)((BitField & ~DisableFastShootingMask) | (value ? DisableFastShootingMask : 0));
        }

        public byte Radius
        {
            get => (byte)((BitField & RadiusMask) >> 3);
            set => BitField = (ushort)((BitField & ~RadiusMask) | ((value << 3) & RadiusMask));
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WeaponBits
    {
        public uint bitfield;

        private const uint ShrapnelMaxMask    = 0b_00000000_00000000_00000000_00011111;
        private const uint ShrapnelRateMask   = 0b_00000000_00000000_00000011_11100000;
        private const uint CloakStatusMask    = 0b_00000000_00000000_00001100_00000000;
        private const uint StealthStatusMask  = 0b_00000000_00000000_00110000_00000000;
        private const uint XRadarStatusMask   = 0b_00000000_00000000_11000000_00000000;
        private const uint AntiWarpStatusMask = 0b_00000000_00000011_00000000_00000000;
        private const uint InitialGunsMask    = 0b_00000000_00001100_00000000_00000000;
        private const uint MaxGunsMask        = 0b_00000000_00110000_00000000_00000000;
        private const uint InitialBombsMask   = 0b_00000000_11000000_00000000_00000000;
        private const uint MaxBombsMask       = 0b_00000011_00000000_00000000_00000000;
        private const uint DoubleBarrelMask   = 0b_00000100_00000000_00000000_00000000;
        private const uint EmpBombMask        = 0b_00001000_00000000_00000000_00000000;
        private const uint SeeMinesMask       = 0b_00010000_00000000_00000000_00000000;

        private uint BitField
        {
            get => LittleEndianConverter.Convert(bitfield);
            set => bitfield = LittleEndianConverter.Convert(value);
        }

        public byte ShrapnelMax
        {
            get => (byte)(BitField & ShrapnelMaxMask);
            set => BitField = (BitField & ~ShrapnelMaxMask) | (value & ShrapnelMaxMask);
        }

        public byte ShrapnelRate
        {
            get => (byte)((BitField & ShrapnelRateMask) >> 5);
            set => BitField = (BitField & ~ShrapnelRateMask) | (((uint)value << 5) & ShrapnelRateMask);
        }

        public byte CloakStatus
        {
            get => (byte)((BitField & CloakStatusMask) >> 10);
            set => BitField = (BitField & ~CloakStatusMask) | (((uint)value << 10) & CloakStatusMask);
        }

        public byte StealthStatus
        {
            get => (byte)((BitField & StealthStatusMask) >> 12);
            set => BitField = (BitField & ~StealthStatusMask) | (((uint)value << 12) & StealthStatusMask);
        }

        public byte XRadarStatus
        {
            get => (byte)((BitField & XRadarStatusMask) >> 14);
            set => BitField = (BitField & ~XRadarStatusMask) | (((uint)value << 14) & XRadarStatusMask);
        }

        public byte AntiWarpStatus
        {
            get => (byte)((BitField & AntiWarpStatusMask) >> 16);
            set => BitField = (BitField & ~AntiWarpStatusMask) | (((uint)value << 16) & AntiWarpStatusMask);
        }

        public byte InitialGuns
        {
            get => (byte)((BitField & InitialGunsMask) >> 18);
            set => BitField = (BitField & ~InitialGunsMask) | (((uint)value << 18) & InitialGunsMask);
        }

        public byte MaxGuns
        {
            get => (byte)((BitField & MaxGunsMask) >> 20);
            set => BitField = (BitField & ~MaxGunsMask) | (((uint)value << 20) & MaxGunsMask);
        }

        public byte InitialBombs
        {
            get => (byte)((BitField & InitialBombsMask) >> 22);
            set => BitField = (BitField & ~InitialBombsMask) | (((uint)value << 22) & InitialBombsMask);
        }

        public byte MaxBombs
        {
            get => (byte)((BitField & MaxBombsMask) >> 24);
            set => BitField = (BitField & ~MaxBombsMask) | (((uint)value << 24) & MaxBombsMask);
        }

        public bool DoubleBarrel
        {
            get => (BitField & DoubleBarrelMask) != 0;
            set => BitField = (BitField & ~DoubleBarrelMask) | (value ? DoubleBarrelMask : 0u);
        }

        public bool EmpBomb
        {
            get => (BitField & EmpBombMask) != 0;
            set => BitField = (BitField & ~EmpBombMask) | (value ? EmpBombMask : 0u);
        }

        public bool SeeMines
        {
            get => (BitField & SeeMinesMask) != 0;
            set => BitField = (BitField & ~SeeMinesMask) | (value ? SeeMinesMask : 0u);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SpawnPositions
    {
        public SpawnPosition SpawnPosition1;
        public SpawnPosition SpawnPosition2;
        public SpawnPosition SpawnPosition3;
        public SpawnPosition SpawnPosition4;

        private Span<SpawnPosition> SpawnPositionSpan => MemoryMarshal.CreateSpan(ref SpawnPosition1, 4);

        public ref SpawnPosition this[int index]
        {
            get
            {
                if (index < 0 || index > 3)
                    throw new IndexOutOfRangeException();

                return ref SpawnPositionSpan[index];
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SpawnPosition
    {
        private uint bitfield;

        private const uint XMask      = 0b_00000000_00000000_00000011_11111111;
        private const uint YMask      = 0b_00000000_00001111_11111100_00000000;
        private const uint RadiusMask = 0b_00011111_11110000_00000000_00000000;

        private uint BitField
        {
            get => LittleEndianConverter.Convert(bitfield);
            set { bitfield = LittleEndianConverter.Convert(value); }
        }

        public ushort X
        {
            get => (ushort)(BitField & XMask);
            set => BitField = (BitField & ~XMask) | (value & XMask);
        }

        public ushort Y
        {
            get => (ushort)((BitField & YMask) >> 10);
            set => BitField = (BitField & ~YMask) | (((uint)value << 10) & YMask);
        }

        public ushort Radius
        {
            get => (ushort)((BitField & RadiusMask) >> 20);
            set => BitField = (BitField & ~RadiusMask) | (((uint)value << 20) & RadiusMask);
        }
    }
}
