using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_SetFreq(short freq)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<C2S_SetFreq>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.SetFreq;
        private readonly short freq = LittleEndianConverter.Convert(freq);

		#region Helper Properties

		public short Freq => LittleEndianConverter.Convert(freq);

		#endregion
	}
}
