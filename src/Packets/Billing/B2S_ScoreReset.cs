using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_ScoreReset
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_ScoreReset>();

        #endregion

        public readonly byte Type;
        private readonly int scoreId;
        private readonly int scoreIdNegative;

        #region Helper Properties

        public int ScoreId => LittleEndianConverter.Convert(scoreId);

        public int ScoreIdNegative => LittleEndianConverter.Convert(scoreIdNegative);

        #endregion
    }
}
