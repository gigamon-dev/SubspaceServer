using System.Runtime.InteropServices;

namespace SS.Utilities.Binary
{
    /// <summary>
    /// Represents a 16-bit unsigned integer stored as little-endian.
    /// </summary>
    /// <remarks>
    /// This helper struct simply wraps a regular <see cref="ushort"/>.
    /// It can be useful for reading/writing data that's specifically transmitted or stored as little-endian (network packets, binary data from a file to be read/written, etc.).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UInt16LittleEndian(ushort value)
    {
        public const int Length = 2;

        private ushort value = LittleEndianConverter.Convert(value);

        public ushort Value
        {
            readonly get => LittleEndianConverter.Convert(value);
            set => this.value = LittleEndianConverter.Convert(value);
        }

        public static implicit operator ushort(UInt16LittleEndian a) => a.Value;
        public static implicit operator UInt16LittleEndian(ushort value) => new(value);
    }
}