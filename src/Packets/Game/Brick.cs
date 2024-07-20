using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_Brick
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<C2S_Brick>();

        #endregion

        public readonly byte Type;
        private readonly short x;
        private readonly short y;

		#region Helper Properties

		public short X => LittleEndianConverter.Convert(x);

		public short Y => LittleEndianConverter.Convert(y);

		#endregion
	}

    /// <summary>
    /// The brick data for a <see cref="S2CPacketType.Brick"/> packet, which consists of the Type (1 byte) followed 1 or more of these.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BrickData(short x1, short y1, short x2, short y2, short freq, short brickId, uint startTime)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<BrickData>();

        #endregion

        private readonly short x1 = LittleEndianConverter.Convert(x1);
        private readonly short y1 = LittleEndianConverter.Convert(y1);
        private readonly short x2 = LittleEndianConverter.Convert(x2);
        private readonly short y2 = LittleEndianConverter.Convert(y2);
        private readonly short freq = LittleEndianConverter.Convert(freq);
        private readonly short brickId = LittleEndianConverter.Convert(brickId);
        private uint startTime = LittleEndianConverter.Convert(startTime);

		#region Helper Properties

		public readonly short X1 => LittleEndianConverter.Convert(x1);

		public readonly short Y1 => LittleEndianConverter.Convert(y1);

		public readonly short X2 => LittleEndianConverter.Convert(x2);

		public readonly short Y2 => LittleEndianConverter.Convert(y2);

		public readonly short Freq => LittleEndianConverter.Convert(freq);

		public readonly short BrickId => LittleEndianConverter.Convert(brickId);

		public uint StartTime
        {
            readonly get => LittleEndianConverter.Convert(startTime);
            set => startTime = LittleEndianConverter.Convert(value);
        }

		#endregion
	}
}
