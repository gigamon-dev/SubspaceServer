using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserLogoff
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

        static S2B_UserLogoff()
        {
            LengthWithScore = Marshal.SizeOf<S2B_UserLogoff>();
            LengthWithoutScore = LengthWithScore - PlayerScore.Length;
        }

        #endregion

        public readonly byte Type;
        private int connectionId;
        private ushort disconnectReason;
        private ushort latency;
        private ushort ping;
        private ushort packetlossS2C;
        private ushort packetlossC2S;
        public PlayerScore Score;

        public S2B_UserLogoff(
            int connectionId, 
            ushort disconnectReason,
            ushort latency,
            ushort ping,
            ushort packetlossS2C,
            ushort packetlossC2S)
        {
            Type = (byte)S2BPacketType.UserLogoff;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            this.disconnectReason = LittleEndianConverter.Convert(disconnectReason);
            this.latency = LittleEndianConverter.Convert(latency);
            this.ping = LittleEndianConverter.Convert(ping);
            this.packetlossS2C = LittleEndianConverter.Convert(packetlossS2C);
            this.packetlossC2S = LittleEndianConverter.Convert(packetlossC2S);
            Score = default;
        }
    }
}
