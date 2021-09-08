using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct BrickPacket
    {
        public readonly byte Type;
        private readonly short x1;
        private readonly short y1;
        private readonly short x2;
        private readonly short y2;
        private readonly short freq;
        private readonly short brickId;
        private readonly uint startTime;

        public short X1
        {
            get { return LittleEndianConverter.Convert(x1); }
        }

        public short Y1
        {
            get { return LittleEndianConverter.Convert(y1); }
        }

        public short X2
        {
            get { return LittleEndianConverter.Convert(x1); }
        }

        public short Y2
        {
            get { return LittleEndianConverter.Convert(y1); }
        }

        public short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
        }

        public short BrickId
        {
            get { return LittleEndianConverter.Convert(brickId); }
        }

        public uint StartTime
        {
            get { return LittleEndianConverter.Convert(startTime); }
        }

        public BrickPacket(short x1, short y1, short x2, short y2, short freq, short brickId, uint startTime)
        {
            Type = (byte)S2CPacketType.Brick;
            this.x1 = LittleEndianConverter.Convert(x1);
            this.y1 = LittleEndianConverter.Convert(y1);
            this.x2 = LittleEndianConverter.Convert(x2);
            this.y2 = LittleEndianConverter.Convert(y2);
            this.freq = LittleEndianConverter.Convert(freq);
            this.brickId = LittleEndianConverter.Convert(brickId);
            this.startTime = LittleEndianConverter.Convert(startTime);
        }
    }
}
