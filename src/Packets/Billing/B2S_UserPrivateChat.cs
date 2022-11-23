using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_UserPrivateChat
    {
        #region Static members

        public static readonly int MinLength;
        public static readonly int MaxLength;
        public static readonly int LengthWithoutText;

        static B2S_UserPrivateChat()
        {
            MaxLength = Marshal.SizeOf<B2S_UserPrivateChat>();
            LengthWithoutText = MaxLength - TextBytesLength;
            MinLength = LengthWithoutText + 1; // at least one byte for text, the null-terminator
        }

        #endregion

        public byte Type;
        private uint sourceServerId;
        public byte SubType;
        public byte Sound;
        private fixed byte textBytes[TextBytesLength];

        #region Helpers

        public uint SourceServerId => LittleEndianConverter.Convert(sourceServerId);

        private const int TextBytesLength = 250;
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], TextBytesLength);

        public Span<byte> GetTextBytes(int packetLength) => TextBytes[..Math.Min(packetLength - LengthWithoutText, TextBytesLength)];

        #endregion
    }
}
