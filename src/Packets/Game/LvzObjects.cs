using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Represents a single LVZ object toggle.
    /// </summary>
    /// <remarks>
    /// A <see cref="S2CPacketType.ToggleLVZ"/> packet contain one or multiple (repeated one after the other).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LvzObjectToggle
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<LvzObjectToggle>();

        #endregion

        private short bitField;

        /// <summary>
        /// Initializes a <see cref="LvzObjectToggle"/>.
        /// </summary>
        /// <param name="id">The Id of the LVZ object.</param>
        /// <param name="isEnabled">Whether the LVZ object is enabled.</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public LvzObjectToggle(short id, bool isEnabled)
        {
            if (id < 0)
                throw new ArgumentOutOfRangeException(nameof(id));

            Id = id;
            IsEnabled = isEnabled;
        }

        #region Helpers

        private short BitField
        {
            get => LittleEndianConverter.Convert(bitField);
            set => bitField = LittleEndianConverter.Convert(value);
        }

        private const ushort IdMask = 0b01111111_11111111;
        private const ushort IsDisabledMask = 0b10000000_00000000;

        /// <summary>
        /// The Id of the LVZ object.
        /// </summary>
        public short Id
        {
            get => (short)(BitField & IdMask);
            set => BitField = (short)((BitField & ~IdMask) | (value & IdMask));
        }

        /// <summary>
        /// Whether the LVZ object is enabled.
        /// </summary>
        /// <remarks>
        /// This is the inverse of the actual bitfield, which is a flag that tells if it's disabled.
        /// However, it's more understandable from a user persepective to be exposed like this.
        /// </remarks>
        public bool IsEnabled
        {
            get => (BitField & IsDisabledMask) == 0;
            set => BitField = (short)((BitField & ~IsDisabledMask) | (value ? 0x0000 : 0x8000));
        }

        #endregion
    }

    /// <summary>
    /// Coordinates on screen relative to reference points.
    /// </summary>
    public enum ScreenOffset : byte
    {
        /// <summary>
        /// Top left corner of screen (no letters in front)
        /// </summary>
        Normal,

        /// <summary>
        /// Screen center
        /// </summary>
        C,

        /// <summary>
        /// Bottom right corner of screen
        /// </summary>
        B,

        /// <summary>
        /// Stats box, lower right corner
        /// </summary>
        S,

        /// <summary>
        /// Top right corner of specials
        /// </summary>
        G,

        /// <summary>
        /// Bottom right corner of specials
        /// </summary>
        F,

        /// <summary>
        /// Below energy bar & spec data
        /// </summary>
        E,

        /// <summary>
        /// Top left corner of chat
        /// </summary>
        T,

        /// <summary>
        /// Top left corner of radar
        /// </summary>
        R,

        /// <summary>
        /// Top left corner of radar's text (clock/location)
        /// </summary>
        O,

        /// <summary>
        /// Top left corner of weapons
        /// </summary>
        W,

        /// <summary>
        /// Bottom left corner of weapons
        /// </summary>
        V,
    }

    /// <summary>
    /// The layer a LVZ object is displayed on. That is, what gets displayed over or under the object.
    /// </summary>
    public enum DisplayLayer : byte
    {
        BelowAll,
        AfterBackground,
        AfterTiles,
        AfterWeapons,
        AfterShips,
        AfterGauges,
        AfterChat,
        TopMost,
    }

    /// <summary>
    /// The mode a LVZ object is displayed. That is, what triggers the object to be displayed.
    /// </summary>
    public enum DisplayMode : byte
    {
        ShowAlways,
        EnterZone,
        EnterArena,
        Kill,
        Death,
        ServerControlled,
    }

    /// <summary>
    /// Data for an LVZ object.
    /// </summary>
    /// <remarks>
    /// In a <see cref="S2CPacketType.ChangeLVZ"/> packet, this used in the data portion.
    /// It is also used within a the LVZ file format.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ObjectData
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<ObjectData>();

        public static ObjectChange CalculateChange(ref ObjectData left, ref ObjectData right)
        {
            ObjectChange change = default;

            if (left.MapX != right.MapX || left.MapY != right.MapY)
            {
                change.Position = true;
            }

            if (left.ImageId != right.ImageId)
            {
                change.Image = true;
            }

            if (left.Layer != right.Layer)
            {
                change.Layer = true;
            }

            if (left.Time != right.Time)
            {
                change.Time = true;
            }

            if (left.Mode != right.Mode)
            {
                change.Mode = true;
            }

            return change;
        }

        #endregion

        private ushort bitField1;
        private ushort bitFieldX;
        private ushort bitFieldY;
        public byte ImageId;
        private byte layer;
        private ushort bitField2;

        #region bitField1

        private ushort BitField1
        {
            get => LittleEndianConverter.Convert(bitField1);
            set => bitField1 = LittleEndianConverter.Convert(value);
        }

        private const ushort IsMapObjectMask = 0b00000000_00000001;
        private const ushort IdMask = 0b11111111_11111110;
        public bool IsMapObject => (BitField1 & IsMapObjectMask) != 0;
        public short Id => (short)((BitField1 & IdMask) >> 1);

        #endregion

        #region bitfieldX and bitFieldY

        private ushort BitFieldX
        {
            get => LittleEndianConverter.Convert(bitFieldX);
            set => bitFieldX = LittleEndianConverter.Convert(value);
        }

        private ushort BitFieldY
        {
            get => LittleEndianConverter.Convert(bitFieldY);
            set => bitFieldY = LittleEndianConverter.Convert(value);
        }

        //
        // Map Object
        //

        public short MapX
        {
            get => (short)BitFieldX;
            set => BitFieldX = (ushort)value;
        }

        public short MapY
        {
            get => (short)BitFieldY;
            set => BitFieldY = (ushort)value;
        }

        //
        // Screen Object
        //

        private const ushort ScreenOffsetMask = 0b_00000000_00001111;
        private const ushort CoordinateMask   = 0b_11111111_11110000;

        public ScreenOffset ScreenXOffset
        {
            get => (ScreenOffset)(BitFieldX & ScreenOffsetMask);
            set => BitFieldX = (ushort)((BitFieldX & ~ScreenOffsetMask) | ((ushort)value & ScreenOffsetMask));
        }

        public short ScreenX
        {
            get => (short)(((BitFieldX & CoordinateMask) << 16) >> 20);
            set => BitFieldX = (ushort)((BitFieldX & ~CoordinateMask) | ((value << 4) & ScreenOffsetMask));
        }

        public ScreenOffset ScreenYOffset
        {
            get => (ScreenOffset)(BitFieldY & ScreenOffsetMask);
            set => BitFieldY = (ushort)((BitFieldY & ~ScreenOffsetMask) | ((ushort)value & ScreenOffsetMask));
        }

        public short ScreenY
        {
            get => (short)(((BitFieldY & CoordinateMask) << 16) >> 20);
            set => BitFieldY = (ushort)((BitFieldY & ~CoordinateMask) | ((value << 4) & ScreenOffsetMask));
        }

        #endregion

        public DisplayLayer Layer
        {
            get => (DisplayLayer)layer;
            set => layer = (byte)value;
        }

        #region bitfield2

        private ushort BitField2
        {
            get => LittleEndianConverter.Convert(bitField2);
            set => bitField2 = LittleEndianConverter.Convert(value);
        }

        private const ushort TimeMask = 0b00001111_11111111;
        private const ushort ModeMask = 0b11110000_00000000;

        public ushort Time
        {
            get => (ushort)(BitField2 & TimeMask);
            set => bitField2 = (ushort)((value & ~TimeMask) | (value & TimeMask));
        }

        public DisplayMode Mode
        {
            get => (DisplayMode)((BitField2 & ModeMask) >> 12);
            set => BitField2 = (ushort)((BitField2 & ~ModeMask) | (((ushort)value << 12) & ModeMask));
        }

        #endregion

        public static bool operator ==(ObjectData a, ObjectData b)
        {
            return a.bitField1 == b.bitField1
                && a.bitFieldX == b.bitFieldX
                && a.bitFieldY == b.bitFieldY
                && a.ImageId == b.ImageId
                && a.layer == b.layer
                && a.bitField2 == b.bitField2;
        }

        public static bool operator !=(ObjectData a, ObjectData b)
        {
            return !(a == b);
        }

        public override bool Equals([NotNullWhen(true)] object obj)
        {
            if (obj is not ObjectData other)
                return false;

            return this == other;
        }

        public override int GetHashCode()
        {
            return Id;
        }
    }

    /// <summary>
    /// Contains flags that tell the LVZ change(s) being made.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ObjectChange
    {
        private byte bitField;

        #region Helper properties

        public byte Value
        {
            get => bitField;
            set => bitField = value;
        }

        private const byte PositionMask = 0b_00000001;
        private const byte ImageMask    = 0b_00000010;
        private const byte LayerMask    = 0b_00000100;
        private const byte TimeMask     = 0b_00001000;
        private const byte ModeMask     = 0b_00010000;

        public bool Position
        {
            set => bitField = (byte)((bitField & ~PositionMask) | (value ? PositionMask : 0));
            get => (bitField & PositionMask) == PositionMask;
        }

        public bool Image
        {
            set => bitField = (byte)((bitField & ~ImageMask) | (value ? ImageMask : 0));
            get => (bitField & ImageMask) == ImageMask;
        }

        public bool Layer
        {
            set => bitField = (byte)((bitField & ~LayerMask) | (value ? LayerMask : 0));
            get => (bitField & LayerMask) == LayerMask;
        }

        public bool Time
        {
            set => bitField = (byte)((bitField & ~TimeMask) | (value ? TimeMask : 0));
            get => (bitField & TimeMask) == TimeMask;
        }

        public bool Mode
        {
            set => bitField = (byte)((bitField & ~ModeMask) | (value ? ModeMask : 0));
            get => (bitField & ModeMask) == ModeMask;
        }

        #endregion
    }

    /// <summary>
    /// Represents a single lvz object modification.
    /// </summary>
    /// <remarks>
    /// A <see cref="S2CPacketType.ChangeLVZ"/> packet can contain one or multiple (repeated one after the other).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LvzObjectChange
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<LvzObjectChange>();

        #endregion

        public ObjectChange Change;
        public ObjectData Data;

        public LvzObjectChange(ObjectChange change, ObjectData data)
        {
            Change = change;
            Data = data;
        }
    }
}
