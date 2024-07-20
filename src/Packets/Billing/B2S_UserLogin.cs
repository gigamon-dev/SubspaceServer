using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    public enum B2SUserLoginResult : byte
    {
        Ok = 0,
        NewUser = 1,
        InvalidPw = 2,
        Banned = 3,
        NoNewConns = 4,
        BadUserName = 5,
        DemoVersion = 6,
        ServerBusy = 7,
        AskDemographics = 8,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_UserLogin
    {
        #region Static Members

        /// <summary>
        /// # of bytes without <see cref="Score"/>.
        /// </summary>
        public static readonly int LengthWithoutScore;

		/// <summary>
		/// # of bytes with a <see cref="PlayerScore"/> (not included in the <see cref="B2S_UserLogin"/> struct).
		/// </summary>
		public static readonly int LengthWithScore;

		static B2S_UserLogin()
        {
			LengthWithoutScore = Marshal.SizeOf<B2S_UserLogin>();
			LengthWithScore = LengthWithoutScore + PlayerScore.Length;
        }

        #endregion

        public readonly byte Type;
        private readonly byte result;
        private readonly int connectionId;
        public readonly NameInlineArray Name;
        public readonly SquadInlineArray Squad;
        public readonly Banner Banner;
        private readonly uint secondsPlayed;
        public readonly FirstLogin FirstLogin;
        private readonly uint Unused0;
        private readonly uint userId;
        private readonly uint Unused1;
		// Optionally followed by a PlayerScore struct for when the result is B2SUserLoginResult.Ok. Purposely not included here.
		//public PlayerScore Score;

		#region Helper Properties

		public B2SUserLoginResult Result => (B2SUserLoginResult)result;

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public TimeSpan Usage => TimeSpan.FromSeconds(LittleEndianConverter.Convert(secondsPlayed));

        public uint UserId => LittleEndianConverter.Convert(userId);

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct NameInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
		}

		[InlineArray(Length)]
		public struct SquadInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
		}

        #endregion
    }

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public readonly struct FirstLogin
	{
		private readonly ushort year;
		private readonly ushort month;
		private readonly ushort day;
		private readonly ushort hour;
		private readonly ushort minute;
		private readonly ushort second;

		public DateTime? ToDateTime()
		{
			if (year == 0 && month == 0 && day == 0)
			{
				// The biller might send all zeros. Consider that to be null.
				return null;
			}
			else
			{
				return new(
					LittleEndianConverter.Convert(year),
					LittleEndianConverter.Convert(month),
					LittleEndianConverter.Convert(day),
					LittleEndianConverter.Convert(hour),
					LittleEndianConverter.Convert(minute),
					LittleEndianConverter.Convert(second));
			}
		}
	}
}
