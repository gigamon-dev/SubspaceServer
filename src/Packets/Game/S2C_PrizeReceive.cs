using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_PrizeReceive(short count, Prize prize)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_PrizeReceive>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.PrizeRecv;
        private readonly short count = LittleEndianConverter.Convert(count);
        private readonly short prize = LittleEndianConverter.Convert((short)prize);

        #region Helper Properties

        public readonly short Count => LittleEndianConverter.Convert(count);

        public readonly Prize Prize => (Prize)LittleEndianConverter.Convert(prize);

        #endregion
    }
}
