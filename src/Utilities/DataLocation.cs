using System;
using System.Buffers.Binary;

namespace SS.Utilities
{
    public struct DataLocation
    {
        public readonly int ByteOffset;
        public readonly int NumBytes;

        public DataLocation(int byteOffset, int numBytes)
        {
            if (byteOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(byteOffset), "Cannot be less than 0.");

            if (numBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(numBytes), "Cannot be less than 0.");

            ByteOffset = byteOffset;
            NumBytes = numBytes;
        }

        public ArraySegment<byte> ToArraySegment(byte[] array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            return new ArraySegment<byte>(array, ByteOffset, NumBytes);
        }

        public Span<byte> Slice(Span<byte> data)
        {
            return data.Slice(ByteOffset, NumBytes);
        }

        public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data)
        {
            return data.Slice(ByteOffset, NumBytes);
        }

        #region Byte

        public byte GetByte(ReadOnlySpan<byte> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            return data[ByteOffset];
        }

        public byte GetByte(ReadOnlySpan<byte> data, int additionalOffset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset + additionalOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            return data[ByteOffset + additionalOffset];
        }

        public void SetByte(Span<byte> data, byte value)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            data[ByteOffset] = value;
        }

        public void SetByte(Span<byte> data, byte value, int additionalOffset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset + additionalOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            data[ByteOffset + additionalOffset] = value;
        }

        #endregion

        #region SByte

        public sbyte GetSByte(ReadOnlySpan<byte> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            return (sbyte)data[ByteOffset];
        }

        public sbyte GetSByte(ReadOnlySpan<byte> data, int additionalOffset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset + additionalOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            return (sbyte)data[ByteOffset + additionalOffset];
        }

        public void SetSByte(Span<byte> data, sbyte value)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            data[ByteOffset] = (byte)value;
        }

        public void SetSByte(Span<byte> data, sbyte value, int additionalOffset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (ByteOffset + additionalOffset >= data.Length)
                throw new ArgumentException($"The location is for a range past the Length.", nameof(data));

            data[ByteOffset + additionalOffset] = (byte)value;
        }

        #endregion

        #region UInt16

        public ushort GetUInt16(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public ushort GetUInt16(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetUInt16(Span<byte> data, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetUInt16(Span<byte> data, ushort value, int additionalOffset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion

        #region Int16

        public short GetInt16(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public short GetInt16(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetInt16(Span<byte> data, short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetInt16(Span<byte> data, short value, int additionalOffset)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion

        #region UInt32

        public uint GetUInt32(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public uint GetUInt32(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetUInt32(Span<byte> data, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetUInt32(Span<byte> data, uint value, int additionalOffset)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion

        #region Int32

        public int GetInt32(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public int GetInt32(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetInt32(Span<byte> data, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetInt32(Span<byte> data, int value, int additionalOffset)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion

        #region UInt64

        public ulong GetUInt64(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public ulong GetUInt64(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetUInt64(Span<byte> data, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetUInt64(Span<byte> data, ulong value, int additionalOffset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion

        #region Int64

        public long GetInt64(ReadOnlySpan<byte> data)
        {
            return BinaryPrimitives.ReadInt64LittleEndian(data.Slice(ByteOffset, NumBytes));
        }

        public long GetInt64(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return BinaryPrimitives.ReadInt64LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes));
        }

        public void SetInt64(Span<byte> data, long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.Slice(ByteOffset, NumBytes), value);
        }

        public void SetInt64(Span<byte> data, long value, int additionalOffset)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.Slice(ByteOffset + additionalOffset, NumBytes), value);
        }

        #endregion
    }

    public struct ByteDataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public ByteDataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 1)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 1.");

            _dataLocation = dataLocation;
        }

        public static explicit operator ByteDataLocation(DataLocation dataLocation)
        {
            return new ByteDataLocation(dataLocation);
        }

        public static implicit operator DataLocation(ByteDataLocation byteDataLocation)
        {
            return byteDataLocation._dataLocation;
        }

        public byte GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetByte(data);
        }

        public byte GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetByte(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, byte value)
        {
            _dataLocation.SetByte(data, value);
        }

        public void SetValue(Span<byte> data, byte value, int additionalOffset)
        {
            _dataLocation.SetByte(data, value, additionalOffset);
        }
    }

    public struct SByteDataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public SByteDataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 1)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 1.");

            _dataLocation = dataLocation;
        }

        public static explicit operator SByteDataLocation(DataLocation dataLocation)
        {
            return new SByteDataLocation(dataLocation);
        }

        public static implicit operator DataLocation(SByteDataLocation sbyteDataLocation)
        {
            return sbyteDataLocation._dataLocation;
        }

        public sbyte GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetSByte(data);
        }

        public sbyte GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetSByte(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, sbyte value)
        {
            _dataLocation.SetSByte(data, value);
        }

        public void SetValue(Span<byte> data, sbyte value, int additionalOffset)
        {
            _dataLocation.SetSByte(data, value, additionalOffset);
        }
    }

    public struct UInt16DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public UInt16DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 2)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 2.");

            _dataLocation = dataLocation;
        }

        public static explicit operator UInt16DataLocation(DataLocation dataLocation)
        {
            return new UInt16DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(UInt16DataLocation uint16DataLocation)
        {
            return uint16DataLocation._dataLocation;
        }

        public ushort GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetUInt16(data);
        }

        public ushort GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetUInt16(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, ushort value)
        {
            _dataLocation.SetUInt16(data, value);
        }

        public void SetValue(Span<byte> data, ushort value, int additionalOffset)
        {
            _dataLocation.SetUInt16(data, value, additionalOffset);
        }
    }

    public struct Int16DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public Int16DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 2)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 2.");

            _dataLocation = dataLocation;
        }

        public static explicit operator Int16DataLocation(DataLocation dataLocation)
        {
            return new Int16DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(Int16DataLocation int16DataLocation)
        {
            return int16DataLocation._dataLocation;
        }

        public short GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetInt16(data);
        }

        public short GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetInt16(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, short value)
        {
            _dataLocation.SetInt16(data, value);
        }

        public void SetValue(Span<byte> data, short value, int additionalOffset)
        {
            _dataLocation.SetInt16(data, value, additionalOffset);
        }
    }

    public struct UInt32DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public UInt32DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 4)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 4.");

            _dataLocation = dataLocation;
        }

        public static explicit operator UInt32DataLocation(DataLocation dataLocation)
        {
            return new UInt32DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(UInt32DataLocation uint32DataLocation)
        {
            return uint32DataLocation._dataLocation;
        }

        public uint GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetUInt32(data);
        }

        public uint GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetUInt32(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, uint value)
        {
            _dataLocation.SetUInt32(data, value);
        }

        public void SetValue(Span<byte> data, uint value, int additionalOffset)
        {
            _dataLocation.SetUInt32(data, value, additionalOffset);
        }
    }

    public struct Int32DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public Int32DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 4)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 4.");

            _dataLocation = dataLocation;
        }

        public static explicit operator Int32DataLocation(DataLocation dataLocation)
        {
            return new Int32DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(Int32DataLocation int32DataLocation)
        {
            return int32DataLocation._dataLocation;
        }

        public int GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetInt32(data);
        }

        public int GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetInt32(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, int value)
        {
            _dataLocation.SetInt32(data, value);
        }

        public void SetValue(Span<byte> data, int value, int additionalOffset)
        {
            _dataLocation.SetInt32(data, value, additionalOffset);
        }
    }

    public struct UInt64DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public UInt64DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 4)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 8.");

            _dataLocation = dataLocation;
        }

        public static explicit operator UInt64DataLocation(DataLocation dataLocation)
        {
            return new UInt64DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(UInt64DataLocation uint64DataLocation)
        {
            return uint64DataLocation._dataLocation;
        }

        public ulong GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetUInt64(data);
        }

        public ulong GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetUInt64(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, ulong value)
        {
            _dataLocation.SetUInt64(data, value);
        }

        public void SetValue(Span<byte> data, ulong value, int additionalOffset)
        {
            _dataLocation.SetUInt64(data, value, additionalOffset);
        }
    }

    public struct Int64DataLocation
    {
        private DataLocation _dataLocation;

        public int ByteOffset
        {
            get { return _dataLocation.ByteOffset; }
        }

        public int NumBytes
        {
            get { return _dataLocation.NumBytes; }
        }

        public Int64DataLocation(DataLocation dataLocation)
        {
            if (dataLocation.NumBytes != 4)
                throw new ArgumentOutOfRangeException(nameof(dataLocation), "NumBytes must be 8.");

            _dataLocation = dataLocation;
        }

        public static explicit operator Int64DataLocation(DataLocation dataLocation)
        {
            return new Int64DataLocation(dataLocation);
        }

        public static implicit operator DataLocation(Int64DataLocation int64DataLocation)
        {
            return int64DataLocation._dataLocation;
        }

        public long GetValue(ReadOnlySpan<byte> data)
        {
            return _dataLocation.GetInt64(data);
        }

        public long GetValue(ReadOnlySpan<byte> data, int additionalOffset)
        {
            return _dataLocation.GetInt64(data, additionalOffset);
        }

        public void SetValue(Span<byte> data, long value)
        {
            _dataLocation.SetInt64(data, value);
        }

        public void SetValue(Span<byte> data, long value, int additionalOffset)
        {
            _dataLocation.SetInt64(data, value, additionalOffset);
        }
    }
}
