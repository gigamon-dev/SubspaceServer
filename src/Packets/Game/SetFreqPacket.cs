using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.C2S
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct SetFreqPacket
    {
        public static readonly int Length;

        static SetFreqPacket()
        {
            Length = Marshal.SizeOf<SetFreqPacket>();
        }

        public readonly byte Type;
        private readonly short freq;

        public short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
        }

        public SetFreqPacket(short freq)
        {
            Type = (byte)C2SPacketType.SetFreq;
            this.freq = LittleEndianConverter.Convert(freq);
        }
    }
}
