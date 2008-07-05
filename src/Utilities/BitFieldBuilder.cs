using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public class BitFieldBuilder
    {
        private byte _nextBit = 0;
        private byte _totalBits;

        public BitFieldBuilder(byte totalBits)
        {
            if((totalBits % 8) != 0)
                throw new ArgumentOutOfRangeException("totalBits", "# of bits in a BitField must be a multiple of 8 (byte boundary)");

            _totalBits += totalBits;
        }

        public BitFieldLocation CreateBitFieldLocation(byte numBits)
        {
            if(numBits <=0)
                throw new ArgumentOutOfRangeException("numBits");

            if((_nextBit + numBits) > 32)
                throw new ArgumentOutOfRangeException("not enough space left in the BitField");

            try
            {
                return new BitFieldLocation(_nextBit, numBits);
            }
            finally
            {
                _nextBit += numBits;
            }
        }
    }
}
