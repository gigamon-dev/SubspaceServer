using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SBrick
    {
        public static readonly int Length;

        static C2SBrick()
        {
            Length = Marshal.SizeOf<C2SBrick>();
        }

        public readonly byte Type;
        private readonly short x;
        private readonly short y;

        public short X
        {
            get { return LittleEndianConverter.Convert(x); }
        }

        public short Y
        {
            get { return LittleEndianConverter.Convert(y); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2CBrick
    {
        public readonly byte Type;
        private readonly short x1;
        private readonly short y1;
        private readonly short x2;
        private readonly short y2;
        private readonly short freq;
        private readonly short brickId;
        private uint startTime;

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
            get => LittleEndianConverter.Convert(startTime);
            set => startTime = LittleEndianConverter.Convert(value);
        }

        public S2CBrick(short x1, short y1, short x2, short y2, short freq, short brickId, uint startTime)
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
