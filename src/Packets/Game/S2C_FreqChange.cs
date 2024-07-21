using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FreqChange(short playerId, short freq)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_FreqChange>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.FreqChange;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);
        private readonly short freq = LittleEndianConverter.Convert(freq);
        private readonly byte dummy = 0xFF; // additional byte which always is set to 0xFF

        #region Helper Properties

        public readonly short PlayerId => LittleEndianConverter.Convert(playerId);

        public readonly short Freq => LittleEndianConverter.Convert(freq);

        #endregion
    }
}
