using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_ServerConnect
    {
        #region Static members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        static S2B_ServerConnect()
        {
            Length = Marshal.SizeOf<S2B_ServerConnect>();
        }

        #endregion

        public readonly byte Type;
        private uint serverId;
        private uint groupId;
        private uint scoreId;
        private fixed byte serverNameBytes[ServerNameBytesLength];
        private ushort port;
        private fixed byte passwordBytes[PasswordBytesLength];

        public S2B_ServerConnect(uint serverId, uint groupId, uint scoreId, ReadOnlySpan<char> serverName, ushort port, ReadOnlySpan<char> password)
        {
            Type = (byte)S2BPacketType.ServerConnect;
            this.serverId = LittleEndianConverter.Convert(serverId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            this.scoreId = LittleEndianConverter.Convert(scoreId);
            this.port = LittleEndianConverter.Convert(port);
            ServerNameBytes.WriteNullPaddedString(serverName.TruncateForEncodedByteLimit(ServerNameBytesLength - 1));
            PasswordBytes.WriteNullPaddedString(password.TruncateForEncodedByteLimit(PasswordBytesLength - 1));
        }

        #region Helpers

        private const int ServerNameBytesLength = 126;
        public Span<byte> ServerNameBytes => MemoryMarshal.CreateSpan(ref serverNameBytes[0], ServerNameBytesLength);


        private const int PasswordBytesLength = 32;
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);

        #endregion
    }
}
