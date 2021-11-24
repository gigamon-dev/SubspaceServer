using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ContinuumVersionPacket
    {
        public byte Type;
        private ushort contVersion;
        private uint checksum;

        public ushort ContVersion
        {
            get { return LittleEndianConverter.Convert(contVersion); }
            set { contVersion = LittleEndianConverter.Convert(value); }
        }

        public uint Checksum
        {
            get { return LittleEndianConverter.Convert(checksum); }
            set { checksum = LittleEndianConverter.Convert(value); }
        }
    }
}
