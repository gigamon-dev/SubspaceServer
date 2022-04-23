using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet sent at the end of a 'speed' game.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_SpeedStats
    {
        #region Static members

        public static readonly int Length;

        static S2C_SpeedStats()
        {
            Length = Marshal.SizeOf(typeof(S2C_SpeedStats));
        }

        #endregion

        public readonly byte Type;

        // personal
        private byte best;
        private ushort rank;
        private uint score;

        // top 5
        private fixed uint scores[5];
        private fixed short playerIds[5];

        #region Helper Properties

        public bool IsPersonalBest => best != 0;
        public ushort Rank => LittleEndianConverter.Convert(rank);
        public uint Score => LittleEndianConverter.Convert(score);
        private Span<uint> Scores => MemoryMarshal.CreateSpan(ref scores[0], 5);
        private Span<short> PlayerIds => MemoryMarshal.CreateSpan(ref playerIds[0], 5);

        #endregion

        #region Helper methods

        public void SetPersonalStats(bool isPersonalBest, ushort rank, uint score)
        {
            best = isPersonalBest ? (byte)1 : (byte)0;
            this.rank = LittleEndianConverter.Convert(rank);
            this.score = LittleEndianConverter.Convert(score);
        }

        public void GetPlayerScore(int index, out short playerId, out uint score)
        {
            if (index < 0 && index >= Scores.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            playerId = LittleEndianConverter.Convert(PlayerIds[index]);
            score = LittleEndianConverter.Convert(Scores[index]);
        }

        public void SetPlayerScore(int index, short playerId, uint score)
        {
            if (index < 0 && index >= Scores.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            PlayerIds[index] = LittleEndianConverter.Convert(playerId);
            Scores[index] = LittleEndianConverter.Convert(score);
        }

        public void ClearPlayerScore(int index)
        {
            if (index < 0 && index >= Scores.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            SetPlayerScore(index, -1, 0);
        }

        #endregion

        public S2C_SpeedStats(bool isPersonalBest, ushort rank, uint score)
        {
            Type = (byte)S2CPacketType.Speed;

            best = isPersonalBest ? (byte)1 : (byte)0;
            this.rank = LittleEndianConverter.Convert(rank);
            this.score = LittleEndianConverter.Convert(score);

            for (int index = 0; index < 5; index++)
            {
                ClearPlayerScore(index);
            }
        }
    }
}
