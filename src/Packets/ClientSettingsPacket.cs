using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ClientSettingsPacket
    {
        public byte Type;

        public ClientBits BitSet;

        // Ship settings
        private const int NumShips = 8;
        private fixed byte shipBytes[144 * NumShips];
        public Span<ShipSettings> Ships => new Span<ShipSettings>(Unsafe.AsPointer(ref shipBytes[0]), NumShips);

        // Int32 settings
        private const int NumInt32Settings = 20;
        private fixed int int32Settings[NumInt32Settings];
        public Span<int> Int32Settings => new Span<int>(Unsafe.AsPointer(ref int32Settings[0]), NumInt32Settings);

        // SpawnPosition settings
        private const int NumSpawnPositions = 4;
        private fixed byte spawnPositionBytes[4 * NumSpawnPositions];
        public Span<SpawnPosition> SpawnPositions => new Span<SpawnPosition>(Unsafe.AsPointer(ref spawnPositionBytes[0]), NumSpawnPositions);

        // Int16 settings
        private const int NumInt16Settings = 58;
        private fixed short int16Settings[NumInt16Settings];
        public Span<short> Int16Settings => new Span<short>(Unsafe.AsPointer(ref int16Settings[0]), NumInt16Settings);

        // Byte settings
        private const int NumByteSettings = 32;
        private fixed byte byteSettings[NumByteSettings];
        public Span<byte> ByteSettings => new Span<byte>(Unsafe.AsPointer(ref byteSettings[0]), NumByteSettings);

        // PrizeWeight settings
        private const int NumPrizeWeightSettings = 28;
        private fixed byte prizeWeightSettings[NumPrizeWeightSettings];
        public Span<byte> PrizeWeightSettings => new Span<byte>(Unsafe.AsPointer(ref prizeWeightSettings[0]), NumPrizeWeightSettings);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClientBits
    {
        private byte bitfield1;
        private byte bitfield2;
        private byte bitfield3;

        // bitfield1 masks
        private const byte ExactDamageMask       = 0b00000001;
        private const byte HideFlagsMask         = 0b00000010;
        private const byte NoXRadarMask          = 0b00000100;
        private const byte SlowFramerateMask     = 0b00111000;
        private const byte DisableScreenshotMask = 0b01000000;

        // bitfield2 masks
        private const byte MaxTimerDriftMask           = 0b00000111;
        private const byte DisableBallThroughWallsMask = 0b00001000;
        private const byte DisableBallKillingMask      = 0b00010000;

        public bool ExactDamage
        {
            get { return (bitfield1 & ExactDamageMask) != 0; }
            set { bitfield1 = (byte)((bitfield1 & ~ExactDamageMask) | ((value ? 1 : 0) & ExactDamageMask)); }
        }

        public bool HideFlags
        {
            get { return ((bitfield1 & HideFlagsMask) >> 1) != 0; }
            set { bitfield1 = (byte)((bitfield1 & ~HideFlagsMask) | (((value ? 1 : 0) << 1) & HideFlagsMask)); }
        }

        public bool NoXRadar
        {
            get { return ((bitfield1 & NoXRadarMask) >> 2) != 0; }
            set { bitfield1 = (byte)((bitfield1 & ~NoXRadarMask) | (((value ? 1 : 0) << 2) & NoXRadarMask)); }
        }

        public byte SlowFramerate
        {
            get { return (byte)((bitfield1 & SlowFramerateMask) >> 3); }
            set { bitfield1 = (byte)((bitfield1 & ~SlowFramerateMask) | ((value << 3) & SlowFramerateMask)); }
        }

        public bool DisableScreenshot
        {
            get { return ((bitfield1 & DisableScreenshotMask) >> 6) != 0; }
            set { bitfield1 = (byte)((bitfield1 & ~DisableScreenshotMask) | (((value ? 1 : 0) << 6) & DisableScreenshotMask)); }
        }

        public byte MaxTimerDrift
        {
            get { return (byte)(bitfield2 & MaxTimerDriftMask); }
            set { bitfield2 = (byte)((bitfield2 & ~MaxTimerDriftMask) | (value & MaxTimerDriftMask)); }
        }

        public bool DisableWallPass
        {
            get { return ((bitfield2 & DisableBallThroughWallsMask) >> 3) != 0; }
            set { bitfield2 = (byte)((bitfield2 & ~DisableBallThroughWallsMask) | (((value ? 1 : 0) << 3) & DisableBallThroughWallsMask)); }
        }

        public bool DisableBallKilling
        {
            get { return ((bitfield2 & DisableBallKillingMask) >> 4) != 0; }
            set { bitfield2 = (byte)((bitfield2 & ~DisableBallKillingMask) | (((value ? 1 : 0) << 4) & DisableBallKillingMask)); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ShipSettings
    {
        // Int32 settings
        private const int NumInt32Settings = 2;
        private fixed int int32Settings[NumInt32Settings];
        public Span<int> Int32Settings => new Span<int>(Unsafe.AsPointer(ref int32Settings[0]), NumInt32Settings);

        // Int16 settings
        private const int NumInt16Settings = 49;
        private fixed short int16Settings[NumInt16Settings];
        public Span<short> Int16Settings => new Span<short>(Unsafe.AsPointer(ref int16Settings[0]), NumInt16Settings);
        public ref MiscBits MiscBits => ref MemoryMarshal.Cast<short, MiscBits>(Int16Settings.Slice(10, 1))[0];

        // Byte settings
        private const int NumByteSettings = 18;
        private fixed byte byteSettings[NumByteSettings];
        public Span<byte> ByteSettings => new Span<byte>(Unsafe.AsPointer(ref byteSettings[0]), NumByteSettings);

        public WeaponBits Weapons;

        private fixed byte padding[16];
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
            get { return LittleEndianConverter.Convert(bitfield); }
            set { bitfield = LittleEndianConverter.Convert(value); }
        }

        public byte SeeBombLevel
        {
            get { return (byte)(BitField & SeeBombLevelMask); }
            set { BitField = (ushort)((BitField & ~SeeBombLevelMask) | (value & SeeBombLevelMask)); }
        }

        public bool DisableFastShooting
        {
            get { return ((BitField & DisableFastShootingMask) >> 2) != 0; }
            set { BitField = (ushort)((BitField & ~DisableFastShootingMask) | (((value ? 1 : 0) << 2) & DisableFastShootingMask)); }
        }

        public byte Radius
        {
            get { return (byte)((BitField & RadiusMask) >> 3); }
            set { BitField = (ushort)((BitField & ~RadiusMask) | ((value << 3) & RadiusMask)); }
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

        public byte ShrapnelMax
        {
            get { return (byte)(bitfield & ShrapnelMaxMask); }
            set { bitfield = (bitfield & ~ShrapnelMaxMask) | (value & ShrapnelMaxMask); }
        }

        public byte ShrapnelRate
        {
            get { return (byte)((bitfield & ShrapnelRateMask) >> 5); }
            set { bitfield = (bitfield & ~ShrapnelRateMask) | (((uint)value << 5) & ShrapnelRateMask); }
        }

        public byte CloakStatus
        {
            get { return (byte)((bitfield & CloakStatusMask) >> 10); }
            set { bitfield = (bitfield & ~CloakStatusMask) | (((uint)value << 10) & CloakStatusMask); }
        }

        public byte StealthStatus
        {
            get { return (byte)((bitfield & StealthStatusMask) >> 12); }
            set { bitfield = (bitfield & ~StealthStatusMask) | (((uint)value << 12) & StealthStatusMask); }
        }

        public byte XRadarStatus
        {
            get { return (byte)((bitfield & XRadarStatusMask) >> 14); }
            set { bitfield = (bitfield & ~XRadarStatusMask) | (((uint)value << 14) & XRadarStatusMask); }
        }

        public byte AntiWarpStatus
        {
            get { return (byte)((bitfield & AntiWarpStatusMask) >> 16); }
            set { bitfield = (bitfield & ~AntiWarpStatusMask) | (((uint)value << 16) & AntiWarpStatusMask); }
        }

        public byte InitialGuns
        {
            get { return (byte)((bitfield & InitialGunsMask) >> 18); }
            set { bitfield = (bitfield & ~InitialGunsMask) | (((uint)value << 18) & InitialGunsMask); }
        }

        public byte MaxGuns
        {
            get { return (byte)((bitfield & MaxGunsMask) >> 20); }
            set { bitfield = (bitfield & ~MaxGunsMask) | (((uint)value << 20) & MaxGunsMask); }
        }

        public byte InitialBombs
        {
            get { return (byte)((bitfield & InitialBombsMask) >> 22); }
            set { bitfield = (bitfield & ~InitialBombsMask) | (((uint)value << 22) & InitialBombsMask); }
        }

        public byte MaxBombs
        {
            get { return (byte)((bitfield & MaxBombsMask) >> 24); }
            set { bitfield = (bitfield & ~MaxBombsMask) | (((uint)value << 24) & MaxBombsMask); }
        }

        public bool DoubleBarrel
        {
            get { return ((bitfield & DoubleBarrelMask) >> 26) != 0; }
            set { bitfield = (bitfield & ~DoubleBarrelMask) | (((value ? 1u : 0u) << 26) & DoubleBarrelMask); }
        }

        public bool EmpBomb
        {
            get { return ((bitfield & EmpBombMask) >> 27) != 0; }
            set { bitfield = (bitfield & ~EmpBombMask) | (((value ? 1u : 0u) << 27) & EmpBombMask); }
        }

        public bool SeeMines
        {
            get { return ((bitfield & SeeMinesMask) >> 28) != 0; }
            set { bitfield = (bitfield & ~SeeMinesMask) | (((value ? 1u : 0u) << 28) & SeeMinesMask); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SpawnPosition
    {
        private uint bitfield;

        private const uint XMask      = 0b_00000000_00000000_00000011_11111111;
        private const uint YMask      = 0b_00000000_00001111_11111100_00000000;
        private const uint RadiusMask = 0b_00011111_11110000_00000000_00000000;

        public uint BitField
        {
            get { return LittleEndianConverter.Convert(bitfield); }
            set { bitfield = LittleEndianConverter.Convert(value); }
        }

        public ushort X
        {
            get { return (ushort)(BitField & XMask); }
            set { BitField = (BitField & ~XMask) | (value & XMask); }
        }

        public ushort Y
        {
            get { return (ushort)((BitField & XMask) >> 10); }
            set { BitField = (BitField & ~XMask) | (((uint)value << 10) & XMask); }
        }

        public ushort Radius
        {
            get { return (ushort)((BitField & RadiusMask) >> 20); }
            set { BitField = (BitField & ~RadiusMask) | (((uint)value << 20) & RadiusMask); }
        }
    }
}
