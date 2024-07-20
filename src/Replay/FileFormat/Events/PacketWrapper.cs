using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketWrapper
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PacketWrapper>();

        #endregion

        public EventHeader Header;
        private ushort dataLength;

        #region Helper properties

        public ushort DataLength
        {
            readonly get => LittleEndianConverter.Convert(dataLength);
            set => dataLength = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
