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
        private int scoreId;
        private int scoreIdNegative;

        #region Helpers

        public int ScoreId => LittleEndianConverter.Convert(scoreId);

        public int ScoreIdNegative => LittleEndianConverter.Convert(scoreIdNegative);

        #endregion
    }
}
