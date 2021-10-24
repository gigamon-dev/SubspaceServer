using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
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

        public readonly byte Type;

        private byte result;
        public B2SUserLoginResult Result => (B2SUserLoginResult)result;

        private int connectionId;
        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        private const int nameBytesLength = 24;
        private fixed byte nameBytes[nameBytesLength];
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], nameBytesLength);

        private const int squadBytesLength = 24;
        private fixed byte squadBytes[squadBytesLength];
        public Span<byte> SquadBytes => MemoryMarshal.CreateSpan(ref squadBytes[0], squadBytesLength);

        public Banner Banner;

        private uint secondsPlayed;
        public TimeSpan Usage => TimeSpan.FromSeconds(LittleEndianConverter.Convert(secondsPlayed));

        public FirstLogin FirstLogin;

        private uint Unused0;

        private uint userId;
        public uint UserId => LittleEndianConverter.Convert(userId);

        private uint Unused1;

        /// <summary>
        /// Only if <see cref="Result"/> = <see cref="B2SUserLoginResult.Ok"/>.
        /// </summary>
        public PlayerScore Score;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FirstLogin // TODO: .NET 6 ISpanFormattable
    {
        private ushort year;
        private ushort month;
        private ushort day;
        private ushort hour;
        private ushort minute;
        private ushort second;

        public DateTime ToDateTime() => new(
            LittleEndianConverter.Convert(year), 
            LittleEndianConverter.Convert(month), 
            LittleEndianConverter.Convert(day), 
            LittleEndianConverter.Convert(hour), 
            LittleEndianConverter.Convert(minute),
            LittleEndianConverter.Convert(second));
    }
}
