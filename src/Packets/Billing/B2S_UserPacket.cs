using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_UserPacket
    {
        public static readonly int MinLength;
        public static readonly int MaxLength;
        public static readonly int LengthWithoutData;

        static B2S_UserPacket()
        {
            MaxLength = Marshal.SizeOf<B2S_UserPacket>();
            LengthWithoutData = MaxLength - dataBytesLength;
            MinLength = LengthWithoutData + 1;
        }

        public byte Type;

        private int connectionId;
        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        private const int dataBytesLength = 1024;
        private fixed byte dataBytes[dataBytesLength];
        public Span<byte> DataBytes => MemoryMarshal.CreateSpan(ref dataBytes[0], dataBytesLength);

        public Span<byte> GetDataBytes(int packetLength) => DataBytes.Slice(0, Math.Min(packetLength - LengthWithoutData, dataBytesLength));
    }
}
