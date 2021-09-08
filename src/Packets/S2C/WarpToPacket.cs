using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct WarpToPacket
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

        public WarpToPacket(short x, short y)
        {
            Type = (byte)S2CPacketType.WarpTo;
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
        }
    }
}
