using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Item in a <see cref="S2CPacketType.PeriodicReward"/> packet.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PeriodicRewardItem
    {
        #region Static members

        public static readonly int Length;

        static PeriodicRewardItem()
        {
            Length = Marshal.SizeOf(typeof(PeriodicRewardItem));
        }

        #endregion

        private short freq;
        private short points;

        public short Freq
        {
            get => LittleEndianConverter.Convert(freq);
            set => freq = LittleEndianConverter.Convert(value);
        }

        public short Points
        {
            get => LittleEndianConverter.Convert(points);
            set => points = LittleEndianConverter.Convert(value);
        }

        public PeriodicRewardItem(short freq, short points)
        {
            this.freq = LittleEndianConverter.Convert(freq);
            this.points = LittleEndianConverter.Convert(points);
        }
    }
}
