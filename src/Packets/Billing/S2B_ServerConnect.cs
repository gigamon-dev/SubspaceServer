using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_ServerConnect
    {
        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        static S2B_ServerConnect()
        {
            Length = Marshal.SizeOf<S2B_ServerConnect>();
        }

        public readonly byte Type;
        private uint serverId;
        private uint groupId;
        private uint scoreId;

        private const int serverNameBytesLength = 126;
        private fixed byte serverNameBytes[serverNameBytesLength];
        public Span<byte> ServerNameBytes => MemoryMarshal.CreateSpan(ref serverNameBytes[0], serverNameBytesLength);

        private ushort port;

        private const int passwordBytesLength = 126;
        private fixed byte passwordBytes[passwordBytesLength];
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], passwordBytesLength);

        public S2B_ServerConnect(uint serverId, uint groupId, uint scoreId, ReadOnlySpan<char> serverName, ushort port, ReadOnlySpan<char> password)
        {
            Type = (byte)S2BPacketType.ServerConnect;
            this.serverId = LittleEndianConverter.Convert(serverId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            this.scoreId = LittleEndianConverter.Convert(scoreId);
            this.port = LittleEndianConverter.Convert(port);
            ServerNameBytes.WriteNullPaddedString(serverName.TruncateForEncodedByteLimit(serverNameBytesLength - 1));
            PasswordBytes.WriteNullPaddedString(password.TruncateForEncodedByteLimit(passwordBytesLength - 1));
        }
    }
}

