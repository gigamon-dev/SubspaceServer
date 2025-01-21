using System.Runtime.InteropServices;

namespace SS.Utilities.Binary
{
    /// <summary>
    /// Represents a 32-bit signed integer stored as little-endian.
    /// </summary>
    /// <remarks>
    /// This helper struct simply wraps a regular <see cref="int"/>.
    /// It can be useful for reading/writing data that's specifically transmitted or stored as little-endian (network packets, binary data from a file to be read/written, etc.).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Int32LittleEndian(int value)
    {
        public const int Length = 4;

        private int value = LittleEndianConverter.Convert(value);

        public int Value
        {
            readonly get => LittleEndianConverter.Convert(value);
            set => this.value = LittleEndianConverter.Convert(value);
        }

        public static implicit operator int(Int32LittleEndian a) => a.Value;
        public static implicit operator Int32LittleEndian(int value) => new(value);
    }
}
