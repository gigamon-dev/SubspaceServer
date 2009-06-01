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

        public ByteDataLocation CreateByteDataLocation()
        {
            return (ByteDataLocation)CreateDataLocation(1);
        }

        public SByteDataLocation CreateSByteDataLocation()
        {
            return (SByteDataLocation)CreateDataLocation(1);
        }

        public UInt16DataLocation CreateUInt16DataLocation()
        {
            return (UInt16DataLocation)CreateDataLocation(2);
        }

        public Int16DataLocation CreateInt16DataLocation()
        {
            return (Int16DataLocation)CreateDataLocation(2);
        }

        public UInt32DataLocation CreateUInt32DataLocation()
        {
            return (UInt32DataLocation)CreateDataLocation(4);
        }

        public Int32DataLocation CreateInt32DataLocation()
        {
            return (Int32DataLocation)CreateDataLocation(4);
        }

        public int NumBytes
        {
            get { return _nextByte; }
        }
    }
}
