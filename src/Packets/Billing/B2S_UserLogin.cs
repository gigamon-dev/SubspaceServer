using SS.Utilities;
using System;
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
    public unsafe struct B2S_UserLogin
    {
        #region Static members

        /// <summary>
        /// # of bytes with <see cref="Score"/>.
        /// </summary>
        public static readonly int LengthWithScore;

        /// <summary>
        /// # of bytes without <see cref="Score"/>.
        /// </summary>
        public static readonly int LengthWithoutScore;

        static B2S_UserLogin()
        {
            LengthWithScore = Marshal.SizeOf<B2S_UserLogin>();
            LengthWithoutScore = LengthWithScore - PlayerScore.Length;
        }

        #endregion

        public readonly byte Type;
        private byte result;
        private int connectionId;
        private fixed byte nameBytes[NameBytesLength];
        private fixed byte squadBytes[SquadBytesLength];
        public Banner Banner;
        private uint secondsPlayed;
        public FirstLogin FirstLogin;
        private uint Unused0;
        private uint userId;
        private uint Unused1;

        /// <summary>
        /// Only if <see cref="Result"/> = <see cref="B2SUserLoginResult.Ok"/>.
        /// </summary>
        public PlayerScore Score;

        #region Helpers

        public B2SUserLoginResult Result => (B2SUserLoginResult)result;

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        private const int NameBytesLength = 24;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        private const int SquadBytesLength = 24;
        public Span<byte> SquadBytes => MemoryMarshal.CreateSpan(ref squadBytes[0], SquadBytesLength);

        public TimeSpan Usage => TimeSpan.FromSeconds(LittleEndianConverter.Convert(secondsPlayed));

        public uint UserId => LittleEndianConverter.Convert(userId);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FirstLogin
    {
        private ushort year;
        private ushort month;
        private ushort day;
        private ushort hour;
        private ushort minute;
        private ushort second;

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
