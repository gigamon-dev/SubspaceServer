using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserPrivateChat
    {
        #region Static members

        /// <summary>
        /// The # of bytes of the packet, excluding the text portion.
        /// </summary>
        public static readonly int LengthWithoutText;

        /// <summary>
        /// The maximum # of characters that can be encoded or decoded from the text portion.
        /// </summary>
        public static readonly int MaxTextChars;

        static S2B_UserPrivateChat()
        {
            LengthWithoutText = Marshal.SizeOf<S2B_UserPrivateChat>() - TextBytesLength;
            MaxTextChars = StringUtils.DefaultEncoding.GetMaxCharCount(TextBytesLength - 1); // -1 for the null-terminator
        }

        #endregion

        public readonly byte Type;
        private int connectionId;
        private uint groupId;
        public byte SubType;
        public byte Sound;
        private fixed byte textBytes[TextBytesLength];

        public S2B_UserPrivateChat(int connectionId, uint groupId, byte subType, byte sound)
        {
            Type = (byte)S2BPacketType.UserPrivateChat;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            SubType = subType;
            Sound = sound;
        }

        #region Helpers

        private const int TextBytesLength = 250;
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], TextBytesLength);
        public int SetText(ReadOnlySpan<char> value) => TextBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(TextBytesLength - 1));

        #endregion
    }
}
