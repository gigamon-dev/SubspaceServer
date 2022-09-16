using System.Runtime.InteropServices;

namespace SS.Utilities.Binary
{
    /// <summary>
    /// Represents a 32-bit unsigned integer stored as little-endian.
    /// </summary>
    /// <remarks>
    /// This helper struct simply wraps a regular <see cref="uint"/>.
    /// It can be useful for reading/writing data that's specifically transmitted or stored as little-endian (network packets, binary data from a file to be read/written, etc.).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UInt32LittleEndian
    {
        public const int Length = 4;

        private uint value;

        public uint Value
        {
            get => LittleEndianConverter.Convert(value);
            set => this.value = LittleEndianConverter.Convert(value);
        }

        public UInt32LittleEndian(uint value)
        {
            this.value = LittleEndianConverter.Convert(value);
        }

        public static implicit operator uint(UInt32LittleEndian a) => a.Value;
        public static implicit operator UInt32LittleEndian(uint value) => new(value);
    }
}
