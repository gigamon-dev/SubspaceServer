using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct ShipChangePacket
    {
        public readonly byte Type;
        public readonly sbyte Ship;
        private readonly short playerId;
        private readonly short freq;

        public readonly short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public readonly short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
        }

        public ShipChangePacket(sbyte ship, short playerId, short freq)
        {
            Type = (byte)S2CPacketType.ShipChange;
            Ship = ship;
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.freq = LittleEndianConverter.Convert(freq);
        }
    }
}
