using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PlayerScore
    {
        #region Static members

        public static readonly int Length;

        static PlayerScore()
        {
            Length = Marshal.SizeOf<PlayerScore>();
        }

        #endregion

        private ushort kills;
        private ushort deaths;
        private ushort flags;
        private int points;
        private int flagPoints;

        public PlayerScore(ushort kills, ushort deaths, ushort flags, int points, int flagPoints)
        {
            this.kills = LittleEndianConverter.Convert(kills);
            this.deaths = LittleEndianConverter.Convert(deaths);
            this.flags = LittleEndianConverter.Convert(flags);
            this.points = LittleEndianConverter.Convert(points);
            this.flagPoints = LittleEndianConverter.Convert(flagPoints);
        }

        #region Helpers

        public ushort Kills
        {
            get => LittleEndianConverter.Convert(kills);
            set => kills = LittleEndianConverter.Convert(value);
        }

        public ushort Deaths
        {
            get => LittleEndianConverter.Convert(deaths);
            set => deaths = LittleEndianConverter.Convert(value);
        }

        public ushort Flags
        {
            get => LittleEndianConverter.Convert(flags);
            set => flags = LittleEndianConverter.Convert(value);
        }

        public int Points
        {
            get => LittleEndianConverter.Convert(points);
            set => points = LittleEndianConverter.Convert(value);
        }

        public int FlagPoints
        {
            get => LittleEndianConverter.Convert(flagPoints);
            set => flagPoints = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
