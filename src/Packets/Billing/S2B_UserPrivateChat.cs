using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserPrivateChat
    {
        /// <summary>
        /// The maximum # of characters that can be encoded or decoded from the text portion.
        /// </summary>
        public static int MaxTextChars => StringUtils.DefaultEncoding.GetMaxCharCount(textBytesLength);

        public static int GetLength(int textLength) => 11 + textLength;

        public readonly byte Type;
        private int connectionId;
        private uint groupId;
        public byte SubType;
        public byte Sound;

        private const int textBytesLength = 250;
        private fixed byte textBytes[textBytesLength];
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], textBytesLength);
        public int SetText(ReadOnlySpan<char> value) => TextBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(textBytesLength - 1));

        public S2B_UserPrivateChat(int connectionId, uint groupId, byte subType, byte sound)
        {
            Type = (byte)S2BPacketType.UserPrivateChat;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            SubType = subType;
            Sound = sound;
        }
    }
}
