using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserCommand
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

        static S2B_UserCommand()
        {
            LengthWithoutText = Marshal.SizeOf<S2B_UserCommand>() - TextBytesLength;
            MaxTextChars = StringUtils.DefaultEncoding.GetMaxCharCount(TextBytesLength - 1); // -1 for the null-terminator
        }

        #endregion

        public readonly byte Type;
        private int connectionId;
        private fixed byte textBytes[TextBytesLength];

        public S2B_UserCommand(int connectionId)
        {
            Type = (byte)S2BPacketType.UserCommand;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }

        #region Helpers

        private const int TextBytesLength = 250;
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], TextBytesLength);

        public int SetText(ReadOnlySpan<char> value, bool addCommandChar)
        {
            Span<byte> textSpan = TextBytes;

            int bytesWritten = 0;

            if (addCommandChar)
            {
                bytesWritten = StringUtils.DefaultEncoding.GetBytes("?", textSpan);
                textSpan = textSpan[bytesWritten..];
            }

            bytesWritten += textSpan.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(textSpan.Length - 1));
            return bytesWritten;
        }

        #endregion
    }
}
