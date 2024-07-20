using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct PlayerScore(ushort kills, ushort deaths, ushort flags, int points, int flagPoints)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<PlayerScore>();

        #endregion

        private readonly ushort kills = LittleEndianConverter.Convert(kills);
        private readonly ushort deaths = LittleEndianConverter.Convert(deaths);
        private readonly ushort flags = LittleEndianConverter.Convert(flags);
        private readonly int points = LittleEndianConverter.Convert(points);
        private readonly int flagPoints = LittleEndianConverter.Convert(flagPoints);

		#region Helper Properties

		public ushort Kills => LittleEndianConverter.Convert(kills);

		public ushort Deaths => LittleEndianConverter.Convert(deaths);

		public ushort Flags => LittleEndianConverter.Convert(flags);

		public int Points => LittleEndianConverter.Convert(points);

		public int FlagPoints => LittleEndianConverter.Convert(flagPoints);

		#endregion
	}
}
