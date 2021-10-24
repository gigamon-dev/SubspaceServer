using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_ScoreReset
    {
        public static readonly int Length;

        static B2S_ScoreReset()
        {
            Length = Marshal.SizeOf<B2S_ScoreReset>();
        }

        public byte Type;

        private uint scoreId;
        public uint ScoreId => LittleEndianConverter.Convert(scoreId);

        private uint scoreIdNegative;
        public uint ScoreIdNegative => LittleEndianConverter.Convert(scoreIdNegative);
    }
}
