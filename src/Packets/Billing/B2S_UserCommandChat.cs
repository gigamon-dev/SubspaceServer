using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_UserCommandChat
    {
        public static readonly int MinLength;
        public static readonly int MaxLength;
        public static readonly int LengthWithoutText;

        static B2S_UserCommandChat()
        {
            MaxLength = Marshal.SizeOf<B2S_UserCommandChat>();
            LengthWithoutText = MaxLength - textBytesLength;
            MinLength = LengthWithoutText + 1;
        }

        public byte Type;

        private int connectionId;
        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        private const int textBytesLength = 250;
        private fixed byte textBytes[textBytesLength];
        public Span<byte> TextBytes => MemoryMarshal.CreateSpan(ref textBytes[0], textBytesLength);

        public Span<byte> GetTextBytes(int packetLength) => TextBytes.Slice(0, Math.Min(packetLength - LengthWithoutText, textBytesLength));
    }
}
