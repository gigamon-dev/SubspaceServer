using System;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Banner
    {
        private const int DataLength = 96;
        private fixed byte dataBytes[DataLength];
        public Span<byte> Data => MemoryMarshal.CreateSpan(ref dataBytes[0], DataLength);

        public bool IsSet
        {
            get
            {
                Span<byte> data = Data;

                for (int i = 0; i < data.Length; i++)
                    if (data[i] != 0)
                        return true;
                
                return false;
            }
        }
    }
}
