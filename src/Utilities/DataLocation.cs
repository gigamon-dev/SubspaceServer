using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public struct DataOffset
    {
        public readonly int ByteOffset;
        public readonly int BitOffset;

        public DataOffset(int byteOffset) : this(byteOffset, 0)
        {
        }

        public DataOffset(int byteOffset, int bitOffset)
        {
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
        }

        public static DataOffset operator +(DataOffset offset1, DataOffset offset2)
        {
            int byteOffset = offset1.ByteOffset + offset2.ByteOffset;
            int bitOffset = offset1.BitOffset + offset2.BitOffset;
            byteOffset += (bitOffset / 8);
            bitOffset %= 8;

            return new DataOffset(byteOffset, bitOffset);
        }
    }

    public struct DataLocation
    {
        public readonly DataOffset Offset;

        public int ByteOffset
        {
            get { return Offset.ByteOffset; }
        }

        public int BitOffset
        {
            get { return Offset.BitOffset; }
        }

        public readonly int NumBits;

        public DataLocation(int byteOffset, int bitOffset, int numBits)
        {
            Offset = new DataOffset(byteOffset, bitOffset);
            NumBits = numBits;
        }

        #region byte

        public byte GetByte(byte[] data)
        {
            return ExtendedBitConverter.ToByte(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public byte GetByte(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToByte(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetByte(byte[] data, byte value)
        {
            ExtendedBitConverter.WriteByteBits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetByte(byte[] data, byte value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteByteBits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator ByteDataLocation(DataLocation dataLocation)
        {
            return new ByteDataLocation(dataLocation);
        }

        #endregion

        #region sbyte

        public sbyte GetSByte(byte[] data)
        {
            return ExtendedBitConverter.ToSByte(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public sbyte GetSByte(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToSByte(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetSByte(byte[] data, sbyte value)
        {
            ExtendedBitConverter.WriteSByteBits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetSByte(byte[] data, sbyte value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteSByteBits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator SByteDataLocation(DataLocation dataLocation)
        {
            return new SByteDataLocation(dataLocation);
        }

        #endregion

        #region uint16

        public ushort GetUInt16(byte[] data)
        {
            return ExtendedBitConverter.ToUInt16(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public ushort GetUInt16(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToUInt16(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetUInt16(byte[] data, ushort value)
        {
            ExtendedBitConverter.WriteUInt16Bits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetUInt16(byte[] data, ushort value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteUInt16Bits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator UInt16DataLocation(DataLocation dataLocation)
        {
            return new UInt16DataLocation(dataLocation);
        }

        #endregion

        #region int16

        public short GetInt16(byte[] data)
        {
            return ExtendedBitConverter.ToInt16(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public short GetInt16(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToInt16(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetInt16(byte[] data, short value)
        {
            ExtendedBitConverter.WriteInt16Bits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetInt16(byte[] data, short value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteInt16Bits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator Int16DataLocation(DataLocation dataLocation)
        {
            return new Int16DataLocation(dataLocation);
        }

        #endregion

        #region uint32

        public uint GetUInt32(byte[] data)
        {
            return ExtendedBitConverter.ToUInt32(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public uint GetUInt32(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToUInt32(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetUInt32(byte[] data, uint value)
        {
            ExtendedBitConverter.WriteUInt32Bits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetUInt32(byte[] data, uint value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteUInt32Bits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator UInt32DataLocation(DataLocation dataLocation)
        {
            return new UInt32DataLocation(dataLocation);
        }

        #endregion

        #region int32

        public int GetInt32(byte[] data)
        {
            return ExtendedBitConverter.ToInt32(data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public int GetInt32(byte[] data, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            return ExtendedBitConverter.ToInt32(data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public void SetInt32(byte[] data, int value)
        {
            ExtendedBitConverter.WriteInt32Bits(value, data, Offset.ByteOffset, Offset.BitOffset, NumBits);
        }

        public void SetInt32(byte[] data, int value, DataOffset additionalOffset)
        {
            additionalOffset += Offset;
            ExtendedBitConverter.WriteInt32Bits(value, data, additionalOffset.ByteOffset, additionalOffset.BitOffset, NumBits);
        }

        public static implicit operator Int32DataLocation(DataLocation dataLocation)
        {
            return new Int32DataLocation(dataLocation);
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public ByteDataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public byte GetValue(byte[] data)
        {
            return _dataLocation.GetByte(data);
        }

        public byte GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetByte(data, additionalOffset);
        }

        public void SetValue(byte[] data, byte value)
        {
            _dataLocation.SetByte(data, value);
        }

        public void SetValue(byte[] data, byte value, DataOffset additionalOffset)
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public SByteDataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public sbyte GetValue(byte[] data)
        {
            return _dataLocation.GetSByte(data);
        }

        public sbyte GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetSByte(data, additionalOffset);
        }

        public void SetValue(byte[] data, sbyte value)
        {
            _dataLocation.SetSByte(data, value);
        }

        public void SetValue(byte[] data, sbyte value, DataOffset additionalOffset)
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public UInt16DataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public ushort GetValue(byte[] data)
        {
            return _dataLocation.GetUInt16(data);
        }

        public ushort GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetUInt16(data, additionalOffset);
        }

        public void SetValue(byte[] data, ushort value)
        {
            _dataLocation.SetUInt16(data, value);
        }

        public void SetValue(byte[] data, ushort value, DataOffset additionalOffset)
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public Int16DataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public short GetValue(byte[] data)
        {
            return _dataLocation.GetInt16(data);
        }

        public short GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetInt16(data, additionalOffset);
        }

        public void SetValue(byte[] data, short value)
        {
            _dataLocation.SetInt16(data, value);
        }

        public void SetValue(byte[] data, short value, DataOffset additionalOffset)
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public UInt32DataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public uint GetValue(byte[] data)
        {
            return _dataLocation.GetUInt32(data);
        }

        public uint GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetUInt32(data, additionalOffset);
        }

        public void SetValue(byte[] data, uint value)
        {
            _dataLocation.SetUInt32(data, value);
        }

        public void SetValue(byte[] data, uint value, DataOffset additionalOffset)
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

        public int BitOffset
        {
            get { return _dataLocation.BitOffset; }
        }

        public Int32DataLocation(DataLocation dataLocation)
        {
            _dataLocation = dataLocation;
        }

        public int GetValue(byte[] data)
        {
            return _dataLocation.GetInt32(data);
        }

        public int GetValue(byte[] data, DataOffset additionalOffset)
        {
            return _dataLocation.GetInt32(data, additionalOffset);
        }

        public void SetValue(byte[] data, int value)
        {
            _dataLocation.SetInt32(data, value);
        }

        public void SetValue(byte[] data, int value, DataOffset additionalOffset)
        {
            _dataLocation.SetInt32(data, value, additionalOffset);
        }
    }
}
