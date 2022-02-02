using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_Brick
    {
        public static readonly int Length;

        static C2S_Brick()
        {
            Length = Marshal.SizeOf<C2S_Brick>();
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

    /// <summary>
    /// Server to client brick data.
    /// </summary>
    /// <remarks>
    /// This packet can support sending data for multiple bricks at once. Though, ASSS and this server sends bricks one by one in their own packet.
    /// TODO: Test sending multiple bricks in 1 packet. The format would be: [Type][Brick0][Brick1]...[BrickN]
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Brick
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

        public S2C_Brick(short x1, short y1, short x2, short y2, short freq, short brickId, uint startTime)
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
