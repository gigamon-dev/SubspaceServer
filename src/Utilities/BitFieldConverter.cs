using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// Contains methods for reading from and writing to bit fields.
    /// </summary>
    public static class BitFieldConverter
    {
        private static uint getUInt32BitFieldMask(byte lowestOrderBit, byte numBits)
        {
            return (uint.MaxValue << (32 - numBits)) >> (32 - (lowestOrderBit + numBits));
        }

        /// <summary>
        /// To read a byte value from an 8 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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

        /// <summary>
        /// To read a byte value from a 16 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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

        /// <summary>
        /// To read a byte value from a 32 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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

        /// <summary>
        /// To read an sbyte value from an 8 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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

        /// <summary>
        /// To read an sbyte value from a 16 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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

        /// <summary>
        /// To read an sbyte value from a 32 bit field.
        /// </summary>
        /// <param name="source">The value to read bits from.</param>
        /// <param name="lowestOrderBit">The lowest order bit to read.</param>
        /// <param name="numBits">The number of bits to read.</param>
        /// <returns>The value of the bit field.</returns>
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
    }
}
