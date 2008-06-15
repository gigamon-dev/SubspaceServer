using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public class DataLocationBuilder
    {
        private int _nextByte = 0;
        private int _nextBit = 0;

        public DataLocation CreateDataLocation(int numBits)
        {
            if (numBits < 1)
                throw new ArgumentOutOfRangeException("must be >= 1", "numBits");

            try
            {
                return new DataLocation(_nextByte, _nextBit, numBits);
            }
            finally
            {
                _nextBit += numBits;
                _nextByte += (int)(_nextBit / 8);
                _nextBit %= 8;
            }
        }

        public int NumBytes
        {
            get
            {
                return (_nextBit > 0) ? _nextByte + 1 : _nextByte;
            }
        }
    }
}
