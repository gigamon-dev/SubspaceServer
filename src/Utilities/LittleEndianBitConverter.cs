using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// Similiar to the <see cref="BitConverter"/> class except it will always do Little-Endian regardless of what architecture being run on.
    /// </summary>
    public static class LittleEndianBitConverter
    {
        #region Read 8-bit

        public static sbyte ToSByte(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            return (sbyte)source[byteOffset];
        }

        public static byte ToByte(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            return source[byteOffset];
        }

        #endregion

        #region Read 16-bit

        public static short ToInt16(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 1 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt16(source, byteOffset);
            else
                return (short)(source[byteOffset] | (source[byteOffset + 1] << 8));
        }

        public static ushort ToUInt16(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 1 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt16(source, byteOffset);
            else
                return (ushort)(source[byteOffset] | (source[byteOffset + 1] << 8));
        }

        #endregion

        #region Read 32-bit

        public static int ToInt32(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 3 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt32(source, byteOffset);
            else
                return source[byteOffset]
                    | (source[byteOffset + 1] << 8)
                    | (source[byteOffset + 2] << 16)
                    | (source[byteOffset + 3] << 24);
        }

        public static uint ToUInt32(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 3 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt32(source, byteOffset);
            else
                return (uint)(source[byteOffset]
                    | (source[byteOffset + 1] << 8)
                    | (source[byteOffset + 2] << 16)
                    | (source[byteOffset + 3] << 24));
        }

        #endregion

        #region Read 64-bit

        public static long ToInt64(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 7 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToInt64(source, byteOffset);
            else
                return (long)source[byteOffset]
                    | ((long)source[byteOffset + 1] << 8)
                    | ((long)source[byteOffset + 2] << 16)
                    | ((long)source[byteOffset + 3] << 24)
                    | ((long)source[byteOffset + 4] << 32)
                    | ((long)source[byteOffset + 5] << 40)
                    | ((long)source[byteOffset + 6] << 48)
                    | ((long)source[byteOffset + 7] << 56);
        }

        public static ulong ToUInt64(byte[] source, int byteOffset)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (byteOffset < 0 || byteOffset + 7 >= source.Length)
                throw new ArgumentException($"{nameof(byteOffset)} is not a valid position to read from {nameof(source)}.");

            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt64(source, byteOffset);
            else
                return ((ulong)source[byteOffset]
                    | ((ulong)source[byteOffset + 1] << 8)
                    | ((ulong)source[byteOffset + 2] << 16)
                    | ((ulong)source[byteOffset + 3] << 24)
                    | ((ulong)source[byteOffset + 4] << 32)
                    | ((ulong)source[byteOffset + 5] << 40)
                    | ((ulong)source[byteOffset + 6] << 48)
                    | ((ulong)source[byteOffset + 7] << 56));
        }

        #endregion

        #region Write 8-bit

        public static bool TryWriteBytes(Span<byte> dest, byte val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 1)
                return false;

            dest[0] = val;
            return true;
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, byte val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset >= dest.Length)
                return false;

            dest[byteOffset] = val;
            return true;
        }

        public static bool TryWriteBytes(Span<byte> dest, sbyte val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 1)
                return false;

            dest[0] = (byte)val;
            return true;
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, sbyte val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset >= dest.Length)
                return false;

            dest[byteOffset] = (byte)val;
            return true;
        }

        #endregion

        #region Write 16-bit

        public static bool TryWriteBytes(Span<byte> dest, ushort val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 2)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, ushort val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 1 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 2), val);
        }

        public static bool TryWriteBytes(Span<byte> dest, short val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 2)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, short val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 1 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 2), val);
        }

        #endregion

        #region Write 32-bit

        public static bool TryWriteBytes(Span<byte> dest, uint val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 4)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                dest[2] = (byte)(val >> 16);
                dest[3] = (byte)(val >> 24);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, uint val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 3 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 4), val);
        }

        public static bool TryWriteBytes(Span<byte> dest, int val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 4)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                dest[2] = (byte)(val >> 16);
                dest[3] = (byte)(val >> 24);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, int val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 3 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 4), val);
        }

        #endregion

        #region Write 64-bit

        public static bool TryWriteBytes(Span<byte> dest, ulong val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 8)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                dest[2] = (byte)(val >> 16);
                dest[3] = (byte)(val >> 24);
                dest[4] = (byte)(val >> 32);
                dest[5] = (byte)(val >> 40);
                dest[6] = (byte)(val >> 48);
                dest[7] = (byte)(val >> 56);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, ulong val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 7 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 8), val);
        }

        public static bool TryWriteBytes(Span<byte> dest, long val)
        {
            if (dest == null || dest.IsEmpty || dest.Length < 8)
                return false;

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.TryWriteBytes(dest, val);
            }
            else
            {
                dest[0] = (byte)val;
                dest[1] = (byte)(val >> 8);
                dest[2] = (byte)(val >> 16);
                dest[3] = (byte)(val >> 24);
                dest[4] = (byte)(val >> 32);
                dest[5] = (byte)(val >> 40);
                dest[6] = (byte)(val >> 48);
                dest[7] = (byte)(val >> 56);
                return true;
            }
        }

        public static bool TryWriteBytes(byte[] dest, int byteOffset, long val)
        {
            if (dest == null)
                return false;

            if (byteOffset < 0 || byteOffset + 7 >= dest.Length)
                return false;

            return TryWriteBytes(new Span<byte>(dest, byteOffset, 8), val);
        }

        #endregion
    }
}
