using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct PrizeReceivePacket
    {
        public readonly byte Type;
        private readonly short count;
        private readonly short prize;

        public readonly short Count
        {
            get { return LittleEndianConverter.Convert(count); }
        }

        public readonly Prize Prize
        {
            get { return (Prize)LittleEndianConverter.Convert(prize); }
        }

        public PrizeReceivePacket(short count, Prize prize)
        {
            Type = (byte)S2CPacketType.PrizeRecv;
            this.count = LittleEndianConverter.Convert(count);
            this.prize = LittleEndianConverter.Convert((short)prize);
        }
    }
}
