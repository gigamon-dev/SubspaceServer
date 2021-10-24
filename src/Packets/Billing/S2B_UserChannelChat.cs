using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserChannelChat
    {
        public static int GetLength(int textBytes) => 5 + channelBytesLength + textBytes;

        public readonly byte Type;
        private int connectionId;

        private const int channelBytesLength = 32;
        private fixed byte channelBytes[channelBytesLength];
        public Span<byte> ChannelBytes => MemoryMarshal.CreateSpan(ref channelBytes[0], channelBytesLength);
        public ReadOnlySpan<char> Channel { set => ChannelBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(channelBytesLength - 1)); }

        private const int textBytesLength = 250;
        private fixed byte textBytes[textBytesLength];
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], textBytesLength);
        public int SetText(ReadOnlySpan<char> value) => TextBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(textBytesLength - 1));

        public S2B_UserChannelChat(int connectionId)
        {
            Type = (byte)S2BPacketType.UserChannelChat;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }
    }
}
