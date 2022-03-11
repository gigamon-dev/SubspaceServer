using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// A single toggled object which a <see cref="S2CPacketType.ToggleObj"/> packet can contain multiple of (repeated one after the other).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ToggledObject
    {
        public static int Length;

        static ToggledObject()
        {
            Length = Marshal.SizeOf<ToggledObject>();
        }

        private short bitField;

        private short BitField
        {
            get => LittleEndianConverter.Convert(bitField);
            set => bitField = LittleEndianConverter.Convert(value);
        }

        private const ushort IdMask         = 0b01111111_11111111;
        private const ushort IsDisabledMask = 0b10000000_00000000;

        public short Id
        {
            get => (short)(BitField & IdMask);
            set => BitField = (short)((BitField & ~IdMask) | (value & IdMask));
        }

        public bool IsDisabled
        {
            get => (BitField & IsDisabledMask) != 0;
            set => BitField = (short)((BitField & ~IsDisabledMask) | (value ? 0x8000 : 0x0000));
        }
    }
}
