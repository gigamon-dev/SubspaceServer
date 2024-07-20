using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Item in a <see cref="S2CPacketType.PeriodicReward"/> packet.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct PeriodicRewardItem(short freq, short points)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PeriodicRewardItem>();

        #endregion

        private readonly short freq = LittleEndianConverter.Convert(freq);
        private readonly short points = LittleEndianConverter.Convert(points);

		#region Helper Properties

		public short Freq => LittleEndianConverter.Convert(freq);

		public short Points => LittleEndianConverter.Convert(points);

		#endregion
	}
}
