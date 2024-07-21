using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserLogoff(
        int connectionId,
        ushort disconnectReason,
        ushort latency,
        ushort ping,
        ushort packetlossS2C,
        ushort packetlossC2S)
    {
        #region Static Members

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

        public readonly byte Type = (byte)S2BPacketType.UserLogoff;
        private readonly int connectionId = LittleEndianConverter.Convert(connectionId);
        private readonly ushort disconnectReason = LittleEndianConverter.Convert(disconnectReason);
        private readonly ushort latency = LittleEndianConverter.Convert(latency);
        private readonly ushort ping = LittleEndianConverter.Convert(ping);
        private readonly ushort packetlossS2C = LittleEndianConverter.Convert(packetlossS2C);
        private readonly ushort packetlossC2S = LittleEndianConverter.Convert(packetlossC2S);
        public PlayerScore Score = default;

        #region Helper Properties

        public readonly int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public readonly ushort DisconnectReason => LittleEndianConverter.Convert(disconnectReason);

        public readonly ushort Latency => LittleEndianConverter.Convert(disconnectReason);

        public readonly ushort Ping => LittleEndianConverter.Convert(disconnectReason);

        public readonly ushort PacketlossS2C => LittleEndianConverter.Convert(disconnectReason);

        public readonly ushort PacketlossC2S => LittleEndianConverter.Convert(disconnectReason);

        #endregion
    }
}
