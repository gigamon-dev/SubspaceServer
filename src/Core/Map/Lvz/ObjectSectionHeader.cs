using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// The header to the object section within a LVZ file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ObjectSectionHeader
    {
        public const uint CLV1 = 0x31564c43;
        public const uint CLV2 = 0x32564c43;

        public static int Length;

        static ObjectSectionHeader()
        {
            Length = Marshal.SizeOf<ObjectSectionHeader>();
        }

        private uint type;
        public uint Type => LittleEndianConverter.Convert(type);

        private uint objectCount;
        public uint ObjectCount => LittleEndianConverter.Convert(objectCount);

        private uint imageCount;
        public uint ImageCount => LittleEndianConverter.Convert(imageCount);
    }
}
