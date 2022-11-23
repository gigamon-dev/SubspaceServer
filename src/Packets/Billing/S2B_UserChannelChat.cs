using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserChannelChat
    {
        #region Static members

        /// <summary>
        /// The # of bytes of the packet, excluding the text portion.
        /// </summary>
        public static readonly int LengthWithoutText;

        static S2B_UserChannelChat()
        {
            LengthWithoutText = Marshal.SizeOf<S2B_UserChannelChat>() - TextBytesLength;
        }

        #endregion

        public readonly byte Type;
        private int connectionId;
        private fixed byte channelBytes[ChannelBytesLength];
        private fixed byte textBytes[TextBytesLength];

        public S2B_UserChannelChat(int connectionId)
        {
            Type = (byte)S2BPacketType.UserChannelChat;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }

        #region Helpers

        private const int ChannelBytesLength = 32;
        public Span<byte> ChannelBytes => MemoryMarshal.CreateSpan(ref channelBytes[0], ChannelBytesLength);
        public ReadOnlySpan<char> Channel { set => ChannelBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(ChannelBytesLength - 1)); }

        private const int TextBytesLength = 250;
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], TextBytesLength);
        public int SetText(ReadOnlySpan<char> value) => TextBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(TextBytesLength - 1));

        #endregion
    }
}
