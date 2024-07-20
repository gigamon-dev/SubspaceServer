using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_SpecRequest(short playerId)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<C2S_SpecRequest>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.SpecRequest;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}
}
