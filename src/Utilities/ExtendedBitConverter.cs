using System;

namespace SS.Utilities
{
    /// <summary>
    /// assuming little endian
    /// signed integers using two's complement
    /// 
    /// TODO: enhance to be able to work for big endian
    /// </summary>
    public static class ExtendedBitConverter
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

        public static sbyte ToSByte(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (bitOffset == 0)
            {
                return (sbyte)data[byteOffset];
            }

            uint mask = getBitOffsetMask(bitOffset);

            return (sbyte)(((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset)));
        }

        public static sbyte ToSByte(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 8)
                return ToSByte(data, byteOffset, bitOffset);

            byte val = ToByte(data, byteOffset, bitOffset, numBits);

            switch (numBits)
            {
                case 1: // when only 1 bit there is no sign bit
                case 8: // all bits used (sign bit is already in the correct spot)
                    return (sbyte)val;
            }

            uint mask = (uint)0x01 << (numBits - 1);
            if ((mask & val) != 0)
            {
                // value has a sign bit that needs to repeated (sign extension)
                return (sbyte)(val | getSignExtensionMask(numBits));
            }
            return (sbyte)val;
        }

        public static byte ToByte(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (bitOffset == 0)
            {
                return data[byteOffset];
            }
            
            uint mask = getBitOffsetMask(bitOffset);

            return (byte)(((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset)));
        }

        public static byte ToByte(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 8)
                return ToByte(data, byteOffset, bitOffset);

            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits <= 0 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            uint mask = getBitOffsetMask(bitOffset);
            int bitsRead = 0;

            uint next = (data[byteOffset] & mask);
            bitsRead += (8 - bitOffset);

            if (numBits <= bitsRead)
            {
                return (byte)(next >> (bitsRead - numBits));
            }

            // move the bits that we've read to the beginning of the byte
            next <<= (8 - bitsRead);

            // read the rest of the byte
            next |= ((uint)data[byteOffset + 1] >> (8 - bitOffset));
            bitsRead += bitOffset;

            if (numBits < bitsRead)
            {
                return (byte)(next >> (bitsRead - numBits));
            }

            return (byte)next;
        }

        #endregion

        #region Int16/UInt16

        public static short ToInt16(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (bitOffset == 0)
                return BitConverter.ToInt16(data, byteOffset);

            uint mask = getBitOffsetMask(bitOffset);

            return (short)((((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset))) |
                ((((data[byteOffset + 1] & mask) << bitOffset) | ((uint)data[byteOffset + 2] >> (8 - bitOffset))) << 8));
        }

        public static short ToInt16(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 16)
                return ToInt16(data, byteOffset, bitOffset);

            ushort val = ToUInt16(data, byteOffset, bitOffset, numBits);

            switch (numBits)
            {
                case 1: // when only 1 bit there is no sign bit
                case 16: // all bits used (sign bit is already in the correct spot)
                    return (short)val;
            }

            uint mask = (uint)0x01 << (numBits - 1);
            if ((mask & val) != 0)
            {
                // value has a sign bit that needs to repeated (sign extension)
                return (short)(val | getSignExtensionMask(numBits));
            }
            return (short)val;
        }

        public static ushort ToUInt16(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (bitOffset == 0)
                return BitConverter.ToUInt16(data, byteOffset);

            uint mask = getBitOffsetMask(bitOffset);

            return (ushort)((((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset))) |
                ((((data[byteOffset + 1] & mask) << bitOffset) | ((uint)data[byteOffset + 2] >> (8 - bitOffset))) << 8));
        }

        public static ushort ToUInt16(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 16)
                return ToUInt16(data, byteOffset, bitOffset);

            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits <= 0 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            uint mask = getBitOffsetMask(bitOffset);

            uint val = 0;
            int bitsRead = 0;

            // TODO: maybe can optimize by not using a loop
            for (int byteIndex = 0; byteIndex <= 2; byteIndex++)
            {
                // read the first part of the byte
                uint next = (data[byteOffset + byteIndex] & mask);
                bitsRead += (8 - bitOffset);

                if (numBits <= bitsRead)
                {
                    next >>= (bitsRead - numBits);
                    return (ushort)(val | (next << (byteIndex * 8)));
                }

                if (bitOffset == 0)
                    continue; // already read the entire byte

                // move the bits that we've read to the beginning of the byte
                next <<= (8 - bitsRead);

                // read the rest of the byte
                next |= ((uint)data[byteOffset + byteIndex + 1] >> (8 - bitOffset));
                bitsRead += bitOffset;

                if (numBits <= bitsRead)
                {
                    next >>= (bitsRead - numBits);
                    return (ushort)(val | (next << (byteIndex * 8)));
                }

                val |= (next << (byteIndex * 8));
            }

            // if we didn't return by this point, there's a problem
            throw new Exception("error reading bits");
        }

        #endregion

        #region Int32/UInt32

        public static int ToInt32(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if(bitOffset == 0)
                return BitConverter.ToInt32(data, byteOffset);

            uint mask = getBitOffsetMask(bitOffset);

            return (int)((((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset))) |
                ((((data[byteOffset + 1] & mask) << bitOffset) | ((uint)data[byteOffset + 2] >> (8 - bitOffset))) << 8) |
                ((((data[byteOffset + 2] & mask) << bitOffset) | ((uint)data[byteOffset + 3] >> (8 - bitOffset))) << 16) |
                ((((data[byteOffset + 3] & mask) << bitOffset) | ((uint)data[byteOffset + 4] >> (8 - bitOffset))) << 24));
        }

        public static int ToInt32(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 32)
                return ToInt32(data, byteOffset, bitOffset);

            uint val = ToUInt32(data, byteOffset, bitOffset, numBits);

            switch (numBits)
            {
                case 1: // when only 1 bit there is no sign bit
                case 32: // all bits used (sign bit is already in the correct spot)
                    return (int)val;
            }

            uint mask = (uint)0x01 << (numBits - 1);
            if ((mask & val) != 0)
            {
                // value has a sign bit that needs to repeated (sign extension)
                return (int)(val | getSignExtensionMask(numBits));
            }
            return (int)val;
        }

        public static uint ToUInt32(byte[] data, int byteOffset, int bitOffset)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (bitOffset == 0)
                return BitConverter.ToUInt32(data, byteOffset);

            uint mask = getBitOffsetMask(bitOffset);

            return (((data[byteOffset] & mask) << bitOffset) | ((uint)data[byteOffset + 1] >> (8 - bitOffset))) |
                ((((data[byteOffset + 1] & mask) << bitOffset) | ((uint)data[byteOffset + 2] >> (8 - bitOffset))) << 8) |
                ((((data[byteOffset + 2] & mask) << bitOffset) | ((uint)data[byteOffset + 3] >> (8 - bitOffset))) << 16) |
                ((((data[byteOffset + 3] & mask) << bitOffset) | ((uint)data[byteOffset + 4] >> (8 - bitOffset))) << 24);
        }

        public static uint ToUInt32(byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (numBits == 32)
                return ToUInt32(data, byteOffset, bitOffset);

            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits <= 0 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");            

            uint mask = getBitOffsetMask(bitOffset);

            uint val = 0;
            int bitsRead = 0;

            // TODO: maybe can optimize by not using a loop
            for (int byteIndex = 0; byteIndex <= 4; byteIndex++)
            {
                // read the first part of the byte
                uint next = (data[byteOffset + byteIndex] & mask);
                bitsRead += (8 - bitOffset);

                if (numBits <= bitsRead)
                {
                    next >>= (bitsRead - numBits);
                    return val | (next << (byteIndex * 8));
                }

                if (bitOffset == 0)
                    continue; // already read the entire byte

                // move the bits that we've read to the beginning of the byte
                next <<= (8 - bitsRead);

                // read the rest of the byte
                next |= ((uint)data[byteOffset + byteIndex + 1] >> (8 - bitOffset));
                bitsRead += bitOffset;

                if (numBits <= bitsRead)
                {
                    next >>= (bitsRead - numBits);
                    return val | (next << (byteIndex * 8));
                }

                val |= (next << (byteIndex * 8));
            }

            // if we didn't return by this point, there's a problem
            throw new Exception("error reading bits");
        }

        #endregion

        #region WriteBits

        public static void WriteByteBits(byte val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            uint v = trimBits(val, numBits);

            int bitsAbleToWrite = 8 - bitOffset;
            if (numBits <= bitsAbleToWrite)
            {
                // value will span a single byte
                int numShifts = bitsAbleToWrite - numBits;
                data[byteOffset] = (byte)((data[byteOffset] & (~(_trimmingMasks[numBits] << numShifts))) | (v << numShifts));
            }
            else
            {
                // value will span parts of two bytes
                int remainingBits = numBits - bitsAbleToWrite;
                data[byteOffset] = (byte)((data[byteOffset] & (~_trimmingMasks[bitsAbleToWrite])) | (v >> remainingBits));

                v = trimBits(v, remainingBits);
                //WriteByteBits((byte)v, data, byteOffset+1, 0, remainingBits); // cheap way of getting the 2nd byte set, probably not very efficient
                data[byteOffset + 1] = (byte)((data[byteOffset+1] & ~(_trimmingMasks[remainingBits] << (8 - remainingBits))) | (v<<(8-remainingBits)));
            }
        }

        public static void WriteSByteBits(sbyte val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            /*
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");
            */
            WriteByteBits((byte)val, data, byteOffset, bitOffset, numBits);
        }

        public static void WriteUInt16Bits(ushort val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");

            if(numBits > 8)
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, 8);
                WriteByteBits((byte)(val>>8), data, byteOffset+1, bitOffset, numBits-8);
            }
            else
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, numBits);
            }
        }

        public static void WriteInt16Bits(short val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            /*
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 16)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-16]");
            */
            WriteUInt16Bits((ushort)val, data, byteOffset, bitOffset, numBits);
        }

        public static void WriteUInt32Bits(uint val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");

            if (numBits > 24)
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, 8);
                WriteByteBits((byte)(val >> 8), data, byteOffset + 1, bitOffset, 8);
                WriteByteBits((byte)(val >> 16), data, byteOffset + 2, bitOffset, 8);
                WriteByteBits((byte)(val >> 24), data, byteOffset + 3, bitOffset, numBits-24);
            }
            else if (numBits > 16)
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, 8);
                WriteByteBits((byte)(val >> 8), data, byteOffset + 1, bitOffset, 8);
                WriteByteBits((byte)(val >> 16), data, byteOffset + 2, bitOffset, numBits-16);
            }
            else if (numBits > 8)
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, 8);
                WriteByteBits((byte)(val >> 8), data, byteOffset + 1, bitOffset, numBits - 8);
            }
            else
            {
                WriteByteBits((byte)val, data, byteOffset, bitOffset, numBits);
            }
        }

        public static void WriteInt32Bits(int val, byte[] data, int byteOffset, int bitOffset, int numBits)
        {
            /*
            if (data == null)
                throw new ArgumentNullException("data");

            if (bitOffset < 0 || bitOffset > 7)
                throw new ArgumentOutOfRangeException("bitOffset", "must be [0-7]");

            if (numBits < 1 || numBits > 32)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-32]");
            */
            WriteUInt32Bits((uint)val, data, byteOffset, bitOffset, numBits);
        }

        #endregion

        #region Bit Field Reading Utility Methods

        private static uint getUInt32BitFieldMask(byte lowestOrderBit, byte numBits)
        {
            return (uint.MaxValue << (32 - numBits)) >> (32 - (lowestOrderBit + numBits));
        }

        public static byte GetByte(byte source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 7)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-7]");

            if(numBits < 1 || numBits > 8)
                throw new ArgumentOutOfRangeException("numBits", "must be [1-8]");

            if ((numBits + lowestOrderBit) > 8)
                throw new ArgumentException("position specified is invalid");

            return (byte)(((uint)source << (32 - (numBits + lowestOrderBit))) >> (32 - numBits));
        }

        public static byte GetByte(ushort source, byte lowestOrderBit, byte numBits)
        {
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
            if (lowestOrderBit < 0 || lowestOrderBit > 16)
                throw new ArgumentOutOfRangeException("lowestOrderBit", "must be [0-16]");

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
