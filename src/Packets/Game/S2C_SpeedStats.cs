using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet sent at the end of a 'speed' game.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_SpeedStats
    {
        #region Static members

        /// <summary>
        /// The # of top scores a <see cref="S2C_SpeedStats"/> can contain.
        /// </summary>
        public const int TopScoreCount = 5;

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
        private int score;

		// top 5
		public ScoresInlineArray TopScores;
		public PlayerIdsInlineArray TopPlayerIds;

		public S2C_SpeedStats(bool isPersonalBest, ushort rank, int score)
		{
			Type = (byte)S2CPacketType.Speed;

			best = isPersonalBest ? (byte)1 : (byte)0;
			this.rank = LittleEndianConverter.Convert(rank);
			this.score = LittleEndianConverter.Convert(score);

			for (int index = 0; index < TopScoreCount; index++)
			{
				ClearTopScore(index);
			}
		}

		#region Helper Properties

		public bool IsPersonalBest => best != 0;
        public ushort Rank => LittleEndianConverter.Convert(rank);
        public int Score => LittleEndianConverter.Convert(score);
        
        #endregion

        #region Helper methods

        public void SetPersonalStats(bool isPersonalBest, ushort rank, int score)
        {
            best = isPersonalBest ? (byte)1 : (byte)0;
            this.rank = LittleEndianConverter.Convert(rank);
            this.score = LittleEndianConverter.Convert(score);
        }

        public void GetTopScore(int index, out short playerId, out int score)
        {
            if (index < 0 && index >= TopScoreCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            playerId = LittleEndianConverter.Convert(TopPlayerIds[index]);
            score = LittleEndianConverter.Convert(TopScores[index]);
        }

        public void SetTopScore(int index, short playerId, int score)
        {
            if (index < 0 && index >= TopScoreCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            TopPlayerIds[index] = LittleEndianConverter.Convert(playerId);
            TopScores[index] = LittleEndianConverter.Convert(score);
        }

        public void ClearTopScore(int index)
        {
            if (index < 0 && index >= TopScoreCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            SetTopScore(index, -1, 0);
        }

        #endregion

		#region Inline Array Types

		[InlineArray(TopScoreCount)]
        public struct ScoresInlineArray
        {
			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private int _element0;
		}

		[InlineArray(TopScoreCount)]
		public struct PlayerIdsInlineArray
		{
			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private short _element0;
		}

        #endregion
    }
}
