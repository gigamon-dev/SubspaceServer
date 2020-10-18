using System;

namespace SS.Utilities
{
    public class DataLocationBuilder
    {
        public DataLocation CreateDataLocation(int numBytes)
        {
            if (numBytes < 1)
                throw new ArgumentOutOfRangeException("Must be >= 1.", nameof(numBytes));

            try
            {
                return new DataLocation(NumBytes, numBytes);
            }
            finally
            {
                NumBytes += numBytes;
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

        public UInt64DataLocation CreateUInt64DataLocation()
        {
            return (UInt64DataLocation)CreateDataLocation(8);
        }

        public Int64DataLocation CreateInt64DataLocation()
        {
            return (Int64DataLocation)CreateDataLocation(8);
        }

        public int NumBytes { get; private set; } = 0;
    }
}
