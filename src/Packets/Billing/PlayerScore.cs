using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PlayerScore
    {
        public static readonly int Length;

        static PlayerScore()
        {
            Length = Marshal.SizeOf<PlayerScore>();
        }

        private ushort kills;
        public ushort Kills
        {
            get => LittleEndianConverter.Convert(kills);
            set => kills = LittleEndianConverter.Convert(value);
        }

        private ushort deaths;
        public ushort Deaths
        {
            get => LittleEndianConverter.Convert(deaths);
            set => deaths = LittleEndianConverter.Convert(value);
        }

        private ushort flags;
        public ushort Flags
        {
            get => LittleEndianConverter.Convert(flags);
            set => flags = LittleEndianConverter.Convert(value);
        }

        private uint points;
        public uint Points
        {
            get => LittleEndianConverter.Convert(points);
            set => points = LittleEndianConverter.Convert(value);
        }

        private uint flagPoints;
        public uint FlagPoints
        {
            get => LittleEndianConverter.Convert(flagPoints);
            set => flagPoints = LittleEndianConverter.Convert(value);
        }

        public PlayerScore(ushort kills, ushort deaths, ushort flags, uint points, uint flagPoints)
        {
            this.kills = LittleEndianConverter.Convert(kills);
            this.deaths = LittleEndianConverter.Convert(deaths);
            this.flags = LittleEndianConverter.Convert(flags);
            this.points = LittleEndianConverter.Convert(points);
            this.flagPoints = LittleEndianConverter.Convert(flagPoints);
        }
    }
}
