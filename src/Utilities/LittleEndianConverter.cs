using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SS.Utilities
{
    /// <summary>
    /// Helper methods for reading and writing data as little-endian.
    /// These methods will reverse endianness when run on big-endian architecture.
    /// </summary>
    public static class LittleEndianConverter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Convert(ushort value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short Convert(short value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Convert(uint value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Convert(int value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Convert(ulong value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Convert(long value)
        {
            return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        }
    }
}
