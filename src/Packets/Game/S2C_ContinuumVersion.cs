using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_ContinuumVersion(ushort contVersion, uint checksum)
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<S2C_ContinuumVersion>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.ContVersion;
		private readonly ushort contVersion = LittleEndianConverter.Convert(contVersion);
		private readonly uint checksum = LittleEndianConverter.Convert(checksum);

		#region Helper Properties

		public ushort ContVersion => LittleEndianConverter.Convert(contVersion);

		public uint Checksum => LittleEndianConverter.Convert(checksum);

		#endregion
	}
}
