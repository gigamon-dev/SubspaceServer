using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public class DataLocationBuilder
    {
        private int _nextByte = 0;

        public DataLocation CreateDataLocation(int numBytes)
        {
            if(numBytes < 1)
                throw new ArgumentOutOfRangeException("must be >= 1", "numBytes");

            try
            {
                return new DataLocation(_nextByte, numBytes);
            }
            finally
            {
                _nextByte += numBytes;
            }
        }

        public int NumBytes
        {
            get { return _nextByte; }
        }
    }
}
