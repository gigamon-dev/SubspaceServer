using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// Similiar to the System.BitConverter class except it will always do Little-Endian regardless of what architecture being run on.
    /// This class also provides methods for writing data into byte arrays whereas System.BitConverter only has methods which create new byte arrays.
    /// 
    /// TODO: support floating point numbers
    /// TODO: support 64 bit bitfields
    /// </summary>
    public static class LittleEndianBitConverter
    {
        #region Masks

        private static readonly byte[] _bitOffsetMasks = 
        {
            0xFF, // 11111111 - 255
            0x7F, // 01111111 - 127
            0x3F, // 00111111 - 63
            0x1F, // 00011111 - 31
            0x0F, // 00001111 - 15
            0x07, // 00000111 - 7
            0x03, // 00000011 - 3
            0x01  // 00000001 - 1
        };

        private static byte getBitOffsetMask(int bitOffset)
        {
            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be in the range of [0-7]");

            //return (byte)((uint)0xFF >> bitOffset);
            return _bitOffsetMasks[bitOffset]; // guessing that the array lookup is fastest

            /*
            switch (bitOffset)
            {
                case 0: return 0xFF;
                case 1: return 0x7F;
                case 2: return 0x3F;
                case 3: return 0x1F;
                case 4: return 0x0F;
                case 5: return 0x07;
                case 6: return 0x03;
                case 7: return 0x01;
                default: throw new ArgumentOutOfRangeException("bitOffset", "must be in the range of [0-7]");
            }
            */
        }

        private static readonly uint[] _signExtensionMasks =
        {
            0xFFFFFFFF,
            0xFFFFFFFE,
            0xFFFFFFFC,
            0xFFFFFFF8,
            0xFFFFFFF0,
            0xFFFFFFE0,
            0xFFFFFFC0,
            0xFFFFFF80,
            0xFFFFFF00,
            0xFFFFFE00,
            0xFFFFFC00,
            0xFFFFF800,
            0xFFFFF000,
            0xFFFFE000,
            0xFFFFC000,
            0xFFFF8000,
            0xFFFF0000,
            0xFFFE0000,
            0xFFFC0000,
            0xFFF80000,
            0xFFF00000,
            0xFFE00000,
            0xFFC00000,
            0xFF800000,
            0xFF000000,
            0xFE000000,
            0xFC000000,
            0xF8000000,
            0xF0000000,
            0xE0000000,
            0xC0000000,
            0x80000000,
        };

        private static uint getSignExtensionMask(int numBits)
        {
            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be in the range [1-32]");

            //return 0xFFFFFFFF << (32 - numBits);
            return _signExtensionMasks[numBits]; // i'm guessing that the array lookup is faster
        }

        private static uint[] _trimmingMasks =
        {
            0x00000000,
            0x00000001,
            0x00000003,
            0x00000007,
            0x0000000F,
            0x0000001F,
            0x0000003F,
            0x0000007F,
            0x000000FF,
            0x000001FF,
            0x000003FF,
            0x000007FF,
            0x00000FFF,
            0x00001FFF,
            0x00003FFF,
            0x00007FFF,
            0x0000FFFF,
            0x0001FFFF,
            0x0003FFFF,
            0x0007FFFF,
            0x000FFFFF,
            0x001FFFFF,
            0x003FFFFF,
            0x007FFFFF,
            0x00FFFFFF,
            0x01FFFFFF,
            0x03FFFFFF,
            0x07FFFFFF,
            0x0FFFFFFF,
            0x1FFFFFFF,
            0x3FFFFFFF,
            0x7FFFFFFF,
            0xFFFFFFFF,
        };

        /// <summary>
        /// To zero out bits that are not in use
        /// </summary>
        /// <param name="val">value to trim</param>
        /// <param name="numBits"># of used bits</param>
        /// <returns></returns>
        private static uint trimBits(uint val, int numBits)
        {
            if (numBits < 0 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [0-32]");

            return val & _trimmingMasks[numBits];
        }

        #endregion

        #region Byte/SByte

        public static sbyte ToSByte(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            return (sbyte)data[byteOffset];
        }

        public static byte ToByte(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            return data[byteOffset];
        }

        #endregion

        #region Int16/UInt16

        public static short ToInt16(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt16(data, byteOffset);
            else
                return (short)(data[byteOffset] | (data[byteOffset + 1] << 8));
        }

        public static ushort ToUInt16(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt16(data, byteOffset);
            else
                return (ushort)(data[byteOffset] | (data[byteOffset + 1] << 8));
        }

        #endregion

        #region Int32/UInt32

        public static int ToInt32(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt32(data, byteOffset);
            else
                return (int)(data[byteOffset] | 
                    (data[byteOffset + 1] << 8) | 
                    (data[byteOffset + 2] << 16) | 
                    (data[byteOffset + 3] << 24));
        }

        public static uint ToUInt32(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt32(data, byteOffset);
            else
                return (uint)(data[byteOffset] | 
                    (data[byteOffset + 1] << 8) | 
                    (data[byteOffset + 2] << 16) | 
                    (data[byteOffset + 3] << 24));
        }

        #endregion

        #region Int64/Uint64

        public static long ToInt64(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt64(data, byteOffset);
            else
                return (long)((ulong)data[byteOffset] |
                    ((ulong)data[byteOffset + 1] << 8) |
                    ((ulong)data[byteOffset + 2] << 16) |
                    ((ulong)data[byteOffset + 3] << 24) |
                    ((ulong)data[byteOffset + 4] << 32) |
                    ((ulong)data[byteOffset + 5] << 40) |
                    ((ulong)data[byteOffset + 6] << 48) |
                    ((ulong)data[byteOffset + 7] << 56));
        }

        public static ulong ToUInt64(byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt64(data, byteOffset);
            else
                return ((ulong)data[byteOffset] |
                    ((ulong)data[byteOffset + 1] << 8) |
                    ((ulong)data[byteOffset + 2] << 16) |
                    ((ulong)data[byteOffset + 3] << 24) |
                    ((ulong)data[byteOffset + 4] << 32) |
                    ((ulong)data[byteOffset + 5] << 40) |
                    ((ulong)data[byteOffset + 6] << 48) |
                    ((ulong)data[byteOffset + 7] << 56));
        }

        #endregion

        #region WriteBits

        public static void WriteByteBits(byte val, byte[] data, int byteOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            data[byteOffset] = val;
        }

        public static void WriteSByteBits(sbyte val, byte[] data, int byteOffset)
        {
            WriteByteBits((byte)val, data, byteOffset);
        }

        public static void WriteUInt16Bits(ushort val, byte[] data, int byteOffset)
        {
            WriteByteBits((byte)val, data, byteOffset);
            WriteByteBits((byte)(val >> 8), data, byteOffset + 1);
        }

        public static void WriteInt16Bits(short val, byte[] data, int byteOffset)
        {
            WriteUInt16Bits((ushort)val, data, byteOffset);
        }

        public static void WriteUInt32Bits(uint val, byte[] data, int byteOffset)
        {
            WriteByteBits((byte)val, data, byteOffset);
            WriteByteBits((byte)(val >> 8), data, byteOffset + 1);
            WriteByteBits((byte)(val >> 16), data, byteOffset + 2);
            WriteByteBits((byte)(val >> 24), data, byteOffset + 3);
        }

        public static void WriteInt32Bits(int val, byte[] data, int byteOffset)
        {
            WriteUInt32Bits((uint)val, data, byteOffset);
        }

        public static void WriteUInt64Bits(ulong val, byte[] data, int byteOffset)
        {
            WriteByteBits((byte)val, data, byteOffset);
            WriteByteBits((byte)(val >> 8), data, byteOffset + 1);
            WriteByteBits((byte)(val >> 16), data, byteOffset + 2);
            WriteByteBits((byte)(val >> 24), data, byteOffset + 3);
            WriteByteBits((byte)(val >> 32), data, byteOffset + 4);
            WriteByteBits((byte)(val >> 40), data, byteOffset + 5);
            WriteByteBits((byte)(val >> 48), data, byteOffset + 6);
            WriteByteBits((byte)(val >> 56), data, byteOffset + 7);
        }

        public static void WriteInt64Bits(long val, byte[] data, int byteOffset)
        {
            WriteUInt64Bits((ulong)val, data, byteOffset);
        }

        #endregion

        #region Bit Field Utility Methods

        private static uint getUInt32BitFieldMask(byte lowestOrderBit, byte numBits)
        {
            return (uint.MaxValue << (32 - numBits)) >> (32 - (lowestOrderBit + numBits));
        }

        public static byte GetByte(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (byte)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static byte GetByte(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (byte)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static byte GetByte(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (byte)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static sbyte GetSByte(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (sbyte)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static sbyte GetSByte(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (sbyte)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static sbyte GetSByte(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (sbyte)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static ushort GetUInt16(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (ushort)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static ushort GetUInt16(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (ushort)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static ushort GetUInt16(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (ushort)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static short GetInt16(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (short)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static short GetInt16(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (short)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static short GetInt16(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (short)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static uint GetUInt32(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (uint)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static uint GetUInt32(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (uint)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static uint GetUInt32(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (uint)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static int GetInt32(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (int)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static int GetInt32(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            return (int)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static int GetInt32(uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            return (int)((source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static byte SetByte(byte value, byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (byte)((((uint)value << lowestOrderBit) & mask) | (source & ~mask));
        }

        public static ushort SetByte(byte value, ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (ushort)((((uint)value << lowestOrderBit) & mask) | (source & ~mask));
        }

        public static uint SetByte(byte value, uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        public static byte SetSByte(sbyte value, byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (byte)((((uint)value << lowestOrderBit) & mask) | (source & ~mask));
        }

        public static ushort SetSByte(sbyte value, ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (ushort)((((uint)value << lowestOrderBit) & mask) | (source & ~mask));
        }

        public static uint SetSByte(sbyte value, uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        public static uint SetUInt16(ushort value, byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        public static uint SetUInt16(ushort value, ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        public static uint SetUInt16(ushort value, uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        public static uint SetUInt32(uint value, byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | ((uint)source & ~mask);
        }

        public static uint SetUInt32(uint value, short source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 15)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-15]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 16)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | ((uint)source & ~mask);
        }

        public static uint SetUInt32(uint value, uint source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 31)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-31]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if ((numBits + lowestOrderBit) > 32)
                throw new ArgumentException("position specified is invalid");

            uint mask = getUInt32BitFieldMask(lowestOrderBit, numBits);
            return (((uint)value << lowestOrderBit) & mask) | (source & ~mask);
        }

        #endregion
    }
}
