using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_BillingIdentity
    {
        public byte Type;

        private const int dataBytesLength = 256;
        private fixed byte dataBytes[dataBytesLength];
        public Span<byte> DataBytes => MemoryMarshal.CreateSpan(ref dataBytes[0], dataBytesLength);

        public Span<byte> GetDataBytes(int packetLength) => DataBytes.Slice(0, Math.Min(packetLength - 1, dataBytesLength));
    }
}
