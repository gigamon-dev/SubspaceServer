using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_SetFreq
    {
        public static readonly int Length;

        static C2S_SetFreq()
        {
            Length = Marshal.SizeOf<C2S_SetFreq>();
        }

        public readonly byte Type;
        private readonly short freq;

        public short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
        }

        public C2S_SetFreq(short freq)
        {
            Type = (byte)C2SPacketType.SetFreq;
            this.freq = LittleEndianConverter.Convert(freq);
        }
    }
}
