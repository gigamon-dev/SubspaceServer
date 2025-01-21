using System.Runtime.InteropServices;

namespace SS.Utilities.Binary
{
    /// <summary>
    /// Represents a 16-bit signed integer stored as little-endian.
    /// </summary>
    /// <remarks>
    /// This helper struct simply wraps a regular <see cref="short"/>.
    /// It can be useful for reading/writing data that's specifically transmitted or stored as little-endian (network packets, binary data from a file to be read/written, etc.).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Int16LittleEndian(short value)
    {
        public const int Length = 2;

        private short value = LittleEndianConverter.Convert(value);

        public short Value
        {
            readonly get => LittleEndianConverter.Convert(value);
            set => this.value = LittleEndianConverter.Convert(value);
        }

        public static implicit operator short(Int16LittleEndian a) => a.Value;
        public static implicit operator Int16LittleEndian(short value) => new(value);
    }
}