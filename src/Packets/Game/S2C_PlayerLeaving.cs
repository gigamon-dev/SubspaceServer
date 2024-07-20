using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_PlayerLeaving(short playerId)
	{
		#region Static Member

		public static readonly int Length = Marshal.SizeOf<S2C_PlayerLeaving>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.PlayerLeaving;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public readonly short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}
}
