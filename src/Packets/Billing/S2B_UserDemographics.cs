using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserDemographics
    {
        public static readonly int LengthWithoutData;

        static S2B_UserDemographics()
        {
            LengthWithoutData = Marshal.SizeOf<S2B_UserDemographics>() - DataLength;
        }

        public byte Type;
        private int connectionId;

        public const int DataLength = 765;
        private fixed byte dataBytes[DataLength];
        public Span<byte> Data => MemoryMarshal.CreateSpan(ref dataBytes[0], DataLength);

        public S2B_UserDemographics(int connectionId)
        {
            Type = (byte)S2BPacketType.UserDemographics;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }
    }
}
