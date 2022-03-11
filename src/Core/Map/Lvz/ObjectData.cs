using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
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
    /// The representation of an object within a LVZ file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ObjectData
    {
        public static int Length;

        static ObjectData()
        {
            Length = Marshal.SizeOf<ObjectData>();
        }

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

        private const ushort ScreenOffsetMask = 0b00000000_00001111;
        private const ushort CoordinateMask = 0b11111111_11110000;

        public ScreenOffset ScreenXType
        {
            get => (ScreenOffset)(BitFieldX & ScreenOffsetMask);
            set => BitFieldX = (ushort)((BitFieldX & ~ScreenOffsetMask) | ((ushort)value & ScreenOffsetMask));
        }

        public short ScreenX
        {
            get => (short)(((short)(BitFieldX & CoordinateMask)) >> 4);
            set => BitFieldX = (ushort)((BitFieldX & ~CoordinateMask) | ((value << 4) & ScreenOffsetMask));
        }

        public ScreenOffset ScreenYType
        {
            get => (ScreenOffset)(BitFieldY & ScreenOffsetMask);
            set => BitFieldY = (ushort)((BitFieldY & ~ScreenOffsetMask) | ((ushort)value & ScreenOffsetMask));
        }

        public short ScreenY
        {
            get => (short)(((short)(BitFieldY & CoordinateMask)) >> 4);
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
    }
}
