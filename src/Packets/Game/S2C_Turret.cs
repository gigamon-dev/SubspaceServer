using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Turret(short playerId, short toPlayerId)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<S2C_Turret>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.Turret;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);
        private readonly short toPlayerId = LittleEndianConverter.Convert(toPlayerId);

		#region Helper Properties

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		public short ToPlayerId => LittleEndianConverter.Convert(toPlayerId);

		#endregion
	}
}
