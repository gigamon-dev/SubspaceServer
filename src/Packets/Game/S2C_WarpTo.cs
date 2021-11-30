using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_WarpTo
    {
        public readonly byte Type;
        private readonly short x;
        private readonly short y;

        public readonly short X
        {
            get { return LittleEndianConverter.Convert(x); }
        }

        public readonly short Y
        {
            get { return LittleEndianConverter.Convert(y); }
        }

        public S2C_WarpTo(short x, short y)
        {
            Type = (byte)S2CPacketType.WarpTo;
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
        }
    }
}
