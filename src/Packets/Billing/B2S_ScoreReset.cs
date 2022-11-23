using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_ScoreReset
    {
        #region Static members

        public static readonly int Length;

        static B2S_ScoreReset()
        {
            Length = Marshal.SizeOf<B2S_ScoreReset>();
        }

        #endregion

        public byte Type;
        private uint scoreId;
        private uint scoreIdNegative;

        #region Helpers

        public uint ScoreId => LittleEndianConverter.Convert(scoreId);

        public uint ScoreIdNegative => LittleEndianConverter.Convert(scoreIdNegative);

        #endregion
    }
}
