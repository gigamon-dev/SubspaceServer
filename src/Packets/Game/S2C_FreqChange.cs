using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FreqChange
    {
        public readonly byte Type;
        private readonly short playerId;
        private readonly short freq;
        private readonly byte dummy; // additional byte which always is set to 0xFF

        public readonly short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public readonly short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
        }

        public S2C_FreqChange(short playerId, short freq)
        {
            Type = (byte)S2CPacketType.FreqChange;
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.freq = LittleEndianConverter.Convert(freq);
            dummy = 0xFF;
        }
    }
}
