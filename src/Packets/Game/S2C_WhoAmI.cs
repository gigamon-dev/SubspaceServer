using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_WhoAmI(short playerId)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<S2C_WhoAmI>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.WhoAmI;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}
}
