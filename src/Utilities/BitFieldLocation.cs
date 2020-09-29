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
            if (numBits <= 0)
                throw new ArgumentOutOfRangeException("numBits", "must be > 0");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
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

        public static explicit operator BoolBitFieldLocation(BitFieldLocation location)
        {
            return new BoolBitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public bool GetValue(byte source)
        {
            return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
        }

        public bool GetValue(ushort source)
        {
            if (NumBits <= 8)
                return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
            else
                return BitFieldConverter.GetUInt16(source, LowestOrderBit, NumBits) != 0;
        }

        public bool GetValue(uint source)
        {
            if (NumBits <= 8)
                return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits) != 0;
            else if (NumBits <= 16)
                return BitFieldConverter.GetUInt16(source, LowestOrderBit, NumBits) != 0;
            else
                return BitFieldConverter.GetUInt32(source, LowestOrderBit, NumBits) != 0;
        }

        public byte SetValue(bool value, byte source)
        {
            return BitFieldConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
        }

        public ushort SetValue(bool value, ushort source)
        {
            return BitFieldConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(bool value, uint source)
        {
            return BitFieldConverter.SetByte(value ? (byte)1 : (byte)0, source, LowestOrderBit, NumBits);
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

        public static explicit operator ByteBitFieldLocation(BitFieldLocation location)
        {
            return new ByteBitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public byte GetValue(byte source)
        {
            return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte GetValue(ushort source)
        {
            return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte GetValue(uint source)
        {
            return BitFieldConverter.GetByte(source, LowestOrderBit, NumBits);
        }

        public byte SetValue(byte value, byte source)
        {
            return BitFieldConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public ushort SetValue(byte value, ushort source)
        {
            return BitFieldConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(byte value, uint source)
        {
            return BitFieldConverter.SetByte(value, source, LowestOrderBit, NumBits);
        }
    }

    // TODO: two's complement
    //public struct SByteBitFieldLocation
    //{
    //    public readonly byte LowestOrderBit;
    //    public readonly byte NumBits;

    //    public SByteBitFieldLocation(byte lowestOrderBit, byte numBits)
    //    {
    //        if (numBits <= 0 || numBits > 16)
    //            throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

    //        LowestOrderBit = lowestOrderBit;
    //        NumBits = numBits;
    //    }

    //    public static explicit operator SByteBitFieldLocation(BitFieldLocation location)
    //    {
    //        return new SByteBitFieldLocation(location.LowestOrderBit, location.NumBits);
    //    }

    //    public sbyte GetValue(byte source)
    //    {
    //        return BitFieldConverter.GetSByte(source, LowestOrderBit, NumBits);
    //    }

    //    public sbyte GetValue(ushort source)
    //    {
    //        return BitFieldConverter.GetSByte(source, LowestOrderBit, NumBits);
    //    }

    //    public sbyte GetValue(uint source)
    //    {
    //        return BitFieldConverter.GetSByte(source, LowestOrderBit, NumBits);
    //    }

    //    public byte SetValue(sbyte value, byte source)
    //    {
    //        return BitFieldConverter.SetSByte(value, source, LowestOrderBit, NumBits);
    //    }

    //    public ushort SetValue(sbyte value, ushort source)
    //    {
    //        return BitFieldConverter.SetSByte(value, source, LowestOrderBit, NumBits);
    //    }

    //    public uint SetValue(sbyte value, uint source)
    //    {
    //        return BitFieldConverter.SetSByte(value, source, LowestOrderBit, NumBits);
    //    }
    //}

    public struct UInt32BitFieldLocation
    {
        public readonly byte LowestOrderBit;
        public readonly byte NumBits;

        public UInt32BitFieldLocation(byte lowestOrderBit, byte numBits)
        {
            if (numBits <= 0 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            LowestOrderBit = lowestOrderBit;
            NumBits = numBits;
        }

        public static explicit operator UInt32BitFieldLocation(BitFieldLocation location)
        {
            return new UInt32BitFieldLocation(location.LowestOrderBit, location.NumBits);
        }

        public uint GetValue(byte source)
        {
            return BitFieldConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint GetValue(ushort source)
        {
            return BitFieldConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint GetValue(uint source)
        {
            return BitFieldConverter.GetUInt32(source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, byte source)
        {
            return BitFieldConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, ushort source)
        {
            return BitFieldConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }

        public uint SetValue(uint value, uint source)
        {
            return BitFieldConverter.SetUInt32(value, source, LowestOrderBit, NumBits);
        }
    }
}
