using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public struct BitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public BitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public byte GetByte(uint source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte SetByte(byte value, byte source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public ushort SetByte(byte value, ushort source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public uint SetByte(byte value, uint source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public uint SetUInt16(ushort value, uint source)
        {
            return LittleEndianBitConverter.SetUInt16(value, source, LowestOrderBit, NumBits);
        }

        public uint SetUInt32(uint value, uint source)
        {
            return LittleEndianBitConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }

        public static explicit operator BoolBitFieldLocation(BitFieldLocation location)
        {
            return new BoolBitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public static explicit operator ByteBitFieldLocation(BitFieldLocation location)
        {
            return new ByteBitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public static explicit operator SByteBitFieldLocation(BitFieldLocation location)
        {
            return new SByteBitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public static explicit operator UInt32BitFieldLocation(BitFieldLocation location)
        {
            return new UInt32BitFieldLocation(location.LowestOrderBit, location.NumBits);
        }
    }

    public struct BoolBitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public BoolBitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            if (numBits <= 0)
                throw new ArgumentOutOfRangeException("numBits", "must be > 0");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public bool GetValue(byte source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
        }

        public bool GetValue(ushort source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
        }

        public bool GetValue(uint source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
        }

        public byte SetValue(bool value, byte source)
        {
            return LittleEndianBitConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
        }

        public ushort SetValue(bool value, ushort source)
        {
            return LittleEndianBitConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(bool value, uint source)
        {
            return LittleEndianBitConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
        }
    }

    public struct ByteBitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public ByteBitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            if (numBits <= 0 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public byte GetValue(byte source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte GetValue(ushort source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte GetValue(uint source)
        {
            return LittleEndianBitConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte SetValue(byte value, byte source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public ushort SetValue(byte value, ushort source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(byte value, uint source)
        {
            return LittleEndianBitConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }
    }

    public struct SByteBitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public SByteBitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            if (numBits <= 0 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public sbyte GetValue(byte source)
        {
            return LittleEndianBitConverter.GetSByte(source, LowestOrderBit, NumBits);
        }

        public sbyte GetValue(ushort source)
        {
            return LittleEndianBitConverter.GetSByte(source, LowestOrderBit, NumBits);
        }

        public sbyte GetValue(uint source)
        {
            return LittleEndianBitConverter.GetSByte(source, LowestOrderBit, NumBits);
        }

        public byte SetValue(sbyte value, byte source)
        {
            return LittleEndianBitConverter.SetSByte(value, source, LowestOrderBit, NumBits);
        }

        public ushort SetValue(sbyte value, ushort source)
        {
            return LittleEndianBitConverter.SetSByte(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(sbyte value, uint source)
        {
            return LittleEndianBitConverter.SetSByte(value, source, LowestOrderBit, NumBits);
        }
    }

    public struct UInt32BitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public UInt32BitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            if (numBits <= 0 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public uint GetValue(byte source)
        {
            return LittleEndianBitConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint GetValue(ushort source)
        {
            return LittleEndianBitConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint GetValue(uint source)
        {
            return LittleEndianBitConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, byte source)
        {
            return LittleEndianBitConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, ushort source)
        {
            return LittleEndianBitConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, uint source)
        {
            return LittleEndianBitConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }
    }
}
