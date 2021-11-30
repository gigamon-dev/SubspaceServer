using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserCommand
    {
        /// <summary>
        /// The maximum # of characters that can be encoded or decoded from the text portion.
        /// </summary>
        public static int MaxTextChars => StringUtils.DefaultEncoding.GetMaxCharCount(textBytesLength);

        public const int HeaderLength = 5;

        public readonly byte Type;
        private int connectionId;

        private const int textBytesLength = 250;
        private fixed byte textBytes[textBytesLength];
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], textBytesLength);

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
            return HeaderLength + bytesWritten;
        }

        public S2B_UserCommand(int connectionId)
        {
            Type = (byte)S2BPacketType.UserCommand;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }
    }
}
