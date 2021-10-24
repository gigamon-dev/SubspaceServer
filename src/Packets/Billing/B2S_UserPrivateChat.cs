using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_UserPrivateChat
    {
        public static readonly int MinLength;
        public static readonly int MaxLength;
        public static readonly int LengthWithoutText;

        static B2S_UserPrivateChat()
        {
            MaxLength = Marshal.SizeOf<B2S_UserPrivateChat>();
            LengthWithoutText = MaxLength - textBytesLength;
            MinLength = LengthWithoutText + 1; // at least one byte for text, the null-terminator
        }

        public byte Type;

        private uint sourceServerId;
        public uint SourceServerId => LittleEndianConverter.Convert(sourceServerId);

        public byte SubType;
        public byte Sound;

        private const int textBytesLength = 250;
        private fixed byte textBytes[textBytesLength];
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], textBytesLength);

        public Span<byte> GetTextBytes(int packetLength) => TextBytes.Slice(0, Math.Min(packetLength - LengthWithoutText, textBytesLength));
    }
}
