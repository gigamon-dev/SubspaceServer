using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public struct DataLocation
    {
        public readonly int ByteOffset;
        public readonly int BitOffset;
        public readonly int NumBits;

        public DataLocation(int byteOffset, int bitOffset, int numBits)
        {
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
            NumBits = numBits;
        }
    }
}
