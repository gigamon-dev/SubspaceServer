using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketWrapper
    {
        #region Static members

        public static readonly int Length;

        static PacketWrapper()
        {
            Length = Marshal.SizeOf(typeof(PacketWrapper));
        }

        #endregion

        public EventHeader Header;
        private ushort dataLength;

        #region Helper properties

        public ushort DataLength
        {
            get => LittleEndianConverter.Convert(dataLength);
            set => dataLength = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
